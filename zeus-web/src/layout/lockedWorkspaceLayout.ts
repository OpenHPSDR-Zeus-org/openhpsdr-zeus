// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Locked-tile size preservation for the FlexWorkspace grid.
//
// The workspace never scrolls: a single uniform `rowHeight` shrinks every tile
// so the whole layout fits the viewport. That shrink also rescales LOCKED
// tiles, which operators expect to stay put at their exact authored size.
//
// A single uniform rowHeight cannot hold one tile fixed while shrinking others,
// so when a tile is locked AND the layout would overflow, we instead pin
// rowHeight at the authored value (locked tiles render at exactly h × authored
// px, invariant to the workspace getting longer/shorter) and shrink only the
// UNLOCKED tiles' grid heights, recompacting around the static locked tiles.
//
// The result is a RENDER layout. The stored layout is never mutated here; the
// component reconciles RGL's echo of this derived layout back to stored
// coordinates so the shrunken render geometry is never persisted
// (reconcileReportedToStored). When nothing is compensated the derived layout
// equals the stored layout and the whole path is a no-op.

import { compactMagnetUp } from './workspaceGrid';
import type { Layout } from 'react-grid-layout';

/** Minimal tile shape the solver needs. `h` should already be clamped to the
 *  panel's maxH (the caller clamps w/h for RGL); `minH` is the panel's
 *  legibility floor. `locked` folds in the workspace-level lock. */
export interface DeriveTile {
  uid: string;
  x: number;
  y: number;
  w: number;
  h: number;
  locked: boolean;
  minH: number;
}

export interface RenderPlacement {
  uid: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface DerivedWorkspaceLayout {
  /** Live grid rowHeight to hand RGL. */
  rowHeight: number;
  /** Live grid vertical margin to hand RGL. */
  rowMargin: number;
  /** Per-tile render placement (same order as input). */
  placements: RenderPlacement[];
  /** True when unlocked tiles were shrunk to keep locked tiles authored-size. */
  compensated: boolean;
  /** Uniform scale applied to unlocked tile heights (1 = untouched). */
  unlockedScale: number;
}

export interface DeriveOptions {
  containerHeight: number;
  /** Authored (maximum) row height in px — the size locked tiles are held at. */
  authoredRowHeightPx: number;
  /** Authored vertical grid margin in px. */
  gridMarginPx: number;
  /** Denominator share used to shrink the row gap on dense layouts. */
  rowGapShare: number;
  /** Baseline target rows the uniform shrink divides into. */
  targetRows: number;
  /** Hard floor for a shrunk rowHeight (matches FlexWorkspace's Math.max). */
  minRowHeightPx: number;
}

function passthrough(t: DeriveTile): RenderPlacement {
  return { uid: t.uid, x: t.x, y: t.y, w: t.w, h: t.h };
}

function maxBottomOf(tiles: readonly { y: number; h: number }[]): number {
  return tiles.reduce((mx, t) => Math.max(mx, t.y + t.h), 0);
}

/** Mirror of FlexWorkspace's rowMargin calc so the uniform fallback matches the
 *  legacy behaviour exactly. */
function uniformRowMargin(
  containerHeight: number,
  targetRows: number,
  gridMarginPx: number,
  rowGapShare: number,
): number {
  if (containerHeight <= 0 || targetRows <= 1) return gridMarginPx;
  return Math.min(gridMarginPx, containerHeight / (targetRows * rowGapShare));
}

/** Mirror of FlexWorkspace's rowHeight calc (containerPadding = 0). */
function uniformRowHeight(
  containerHeight: number,
  targetRows: number,
  rowMargin: number,
  authoredRowHeightPx: number,
  minRowHeightPx: number,
): number {
  if (containerHeight <= 0) return authoredRowHeightPx;
  const inner = containerHeight - rowMargin * Math.max(0, targetRows - 1);
  return Math.min(
    authoredRowHeightPx,
    Math.max(minRowHeightPx, inner / Math.max(1, targetRows)),
  );
}

function uniformResult(
  tiles: DeriveTile[],
  layoutRows: number,
  opts: DeriveOptions,
): DerivedWorkspaceLayout {
  const targetRows = Math.max(layoutRows, opts.targetRows);
  const rowMargin = uniformRowMargin(
    opts.containerHeight,
    targetRows,
    opts.gridMarginPx,
    opts.rowGapShare,
  );
  const rowHeight = uniformRowHeight(
    opts.containerHeight,
    targetRows,
    rowMargin,
    opts.authoredRowHeightPx,
    opts.minRowHeightPx,
  );
  return {
    rowHeight,
    rowMargin,
    placements: tiles.map(passthrough),
    compensated: false,
    unlockedScale: 1,
  };
}

/** Render the layout with unlocked tile heights scaled by `scale`, recompacted
 *  so unlocked tiles magnet up around the static (locked) tiles. */
function renderAtScale(
  tiles: DeriveTile[],
  scale: number,
): { placements: RenderPlacement[]; maxBottom: number } {
  const items: Layout = tiles.map((t) => ({
    i: t.uid,
    x: t.x,
    y: t.y,
    w: t.w,
    h: t.locked
      ? t.h
      : Math.max(t.minH, Math.min(t.h, Math.round(t.h * scale))),
    static: t.locked,
    moved: false,
  }));
  const compacted = compactMagnetUp(items);
  return {
    placements: compacted.map((it) => ({
      uid: it.i,
      x: it.x,
      y: it.y,
      w: it.w,
      h: it.h,
    })),
    maxBottom: maxBottomOf(compacted),
  };
}

/** Find the largest unlocked-height scale that keeps the compacted layout
 *  within `fitRows`. Returns null when even the minimum heights overflow
 *  (locked tiles alone exceed the viewport). */
function fitUnlockedToRows(
  tiles: DeriveTile[],
  fitRows: number,
): { placements: RenderPlacement[]; scale: number } | null {
  const full = renderAtScale(tiles, 1);
  if (full.maxBottom <= fitRows) {
    return { placements: full.placements, scale: 1 };
  }

  // Binary-search the largest scale in (0, 1) that fits. 24 iterations resolves
  // far finer than the 1-row grid quantisation.
  let lo = 0;
  let hi = 1;
  let best: { placements: RenderPlacement[]; scale: number } | null = null;
  for (let i = 0; i < 24; i += 1) {
    const mid = (lo + hi) / 2;
    const r = renderAtScale(tiles, mid);
    if (r.maxBottom <= fitRows) {
      best = { placements: r.placements, scale: mid };
      lo = mid;
    } else {
      hi = mid;
    }
  }
  if (best) return best;

  // Floor: every unlocked tile at its minH. If that still overflows, the locked
  // tiles themselves don't fit — caller falls back to a uniform shrink.
  const floor = renderAtScale(tiles, 0);
  if (floor.maxBottom <= fitRows) {
    return { placements: floor.placements, scale: 0 };
  }
  return null;
}

/**
 * Compute the workspace render layout. When a tile is locked and the layout
 * would overflow, locked tiles are held at authored size (rowHeight pinned) and
 * unlocked tiles shrink to fit. Otherwise this reproduces the legacy uniform
 * shrink-to-fit exactly.
 */
export function deriveWorkspaceLayout(
  tiles: DeriveTile[],
  opts: DeriveOptions,
): DerivedWorkspaceLayout {
  const layoutRows = maxBottomOf(tiles);
  const anyLocked = tiles.some((t) => t.locked);
  const anyUnlocked = tiles.some((t) => !t.locked);

  // Nothing locked, no measurable container, or every tile locked (no unlocked
  // tile can absorb the slack) → legacy uniform behaviour.
  if (!anyLocked || !anyUnlocked || opts.containerHeight <= 0) {
    return uniformResult(tiles, layoutRows, opts);
  }

  const { authoredRowHeightPx: R, gridMarginPx: m } = opts;
  const fitRows = Math.floor((opts.containerHeight + m) / (R + m));
  if (fitRows <= 0) {
    return uniformResult(tiles, layoutRows, opts);
  }

  // Already fits at authored density → render as stored at the authored
  // rowHeight. Locked tiles are exactly h × R px; spare height stays at the
  // bottom (matching the sparse-layout behaviour the workspace already has).
  if (layoutRows <= fitRows) {
    return {
      rowHeight: R,
      rowMargin: m,
      placements: tiles.map(passthrough),
      compensated: false,
      unlockedScale: 1,
    };
  }

  // Overflow: shrink unlocked tiles to fit while locked tiles stay authored.
  const fitted = fitUnlockedToRows(tiles, fitRows);
  if (fitted) {
    return {
      rowHeight: R,
      rowMargin: m,
      placements: fitted.placements,
      compensated: fitted.scale < 1,
      unlockedScale: fitted.scale,
    };
  }

  // Locked tiles alone overflow the viewport — unavoidable corner. Fall back to
  // the uniform shrink (locked tiles shrink too) rather than show a scrollbar.
  return uniformResult(tiles, layoutRows, opts);
}

/**
 * Reconcile the layout RGL reports back into stored coordinates so the shrunken
 * render geometry is never persisted.
 *
 * - Not compensated: render == stored, pass the report through untouched
 *   (byte-identical to the legacy persistence path).
 * - Compensated echo (report matches what we fed RGL): restore the tile's
 *   stored geometry so the store diff sees no change.
 * - Compensated genuine edit (report diverges from the derived layout):
 *   keep the horizontal placement, un-scale the height back toward stored
 *   density so a drag/resize during a shrink doesn't persist a shrunken size.
 */
export function reconcileReportedToStored(
  reported: Layout,
  derived: DerivedWorkspaceLayout,
  storedByUid: Map<string, { x: number; y: number; w: number; h: number }>,
): Layout {
  if (!derived.compensated) return reported;
  const derivedByUid = new Map(derived.placements.map((p) => [p.uid, p]));
  return reported.map((item) => {
    const stored = storedByUid.get(item.i);
    const d = derivedByUid.get(item.i);
    if (!stored) return item;
    if (
      d &&
      item.x === d.x &&
      item.y === d.y &&
      item.w === d.w &&
      item.h === d.h
    ) {
      // Pure echo of the derived layout → persist stored geometry (no change).
      return { ...item, x: stored.x, y: stored.y, w: stored.w, h: stored.h };
    }
    // Genuine edit while compensated. Horizontal passes through; un-scale the
    // height so a shrunken render size is not written back.
    const h =
      derived.unlockedScale > 0
        ? Math.max(1, Math.round(item.h / derived.unlockedScale))
        : item.h;
    return { ...item, h };
  });
}
