// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Locked-tile size preservation for the FlexWorkspace grid.
//
// The workspace never scrolls: a single uniform `rowHeight` shrinks every tile
// so the whole layout fits the viewport. That shrink also rescales LOCKED
// tiles, which operators expect to stay put at the exact size they had when
// they pinned them.
//
// Locking must be INERT — clicking the lock must not change the panel's size —
// AND the size must then stay fixed as the workspace grows/shrinks. A single
// uniform rowHeight can't do both, so each locked tile carries the on-screen
// pixel height it had at lock time (`lockedHeightPx`, captured by the
// component). We then pick the largest rowHeight (<= authored) at which the
// whole layout still fits, holding each locked tile at its frozen pixel height
// via a compensated (fractional) grid span and letting the UNLOCKED tiles take
// the remaining space. Because reserving a locked tile's *current* height
// changes nothing, the solver lands on the pre-lock rowHeight at the moment of
// locking (inert); as the workspace later grows, only the rowHeight — and thus
// the unlocked tiles — shrink, while locked tiles keep their frozen height.
//
// The result is a RENDER layout. The stored layout is never mutated here; the
// component reconciles RGL's echo of this derived layout back to stored
// coordinates so the compensated render geometry is never persisted
// (reconcileReportedToStored). With nothing locked the derived layout equals
// the stored layout and the whole path is a no-op.

import type { Layout, LayoutItem } from 'react-grid-layout';

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
  /** On-screen pixel height captured when the tile was locked. Locked tiles are
   *  held at exactly this height. Absent for tiles locked before the field
   *  existed — those fall back to their height at the no-lock density so
   *  locking stays inert. */
  lockedHeightPx?: number;
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

/** On-screen pixel height of a tile spanning `h` grid rows. Mirrors RGL's
 *  calcGridItemWHPx (minus the final px rounding, which we don't need here). */
function tileHeightPx(h: number, rowHeight: number, margin: number): number {
  return rowHeight * h + Math.max(0, h - 1) * margin;
}

/** Pixel bottom edge of a tile, matching RGL's absoluteStrategy positioning. */
function tileBottomPx(
  y: number,
  h: number,
  rowHeight: number,
  margin: number,
): number {
  return (rowHeight + margin) * y + tileHeightPx(h, rowHeight, margin);
}

/** Grid rows a tile must span to render exactly `px` tall at `rowHeight`:
 *  solve px = rowHeight·h + (h-1)·margin  ⇒  h = (px + margin)/(rowHeight + margin). */
function lockedSpanRows(px: number, rowHeight: number, margin: number): number {
  return (px + margin) / (rowHeight + margin);
}

/** Render the layout at a candidate rowHeight: each locked tile gets a
 *  (fractional) span that reproduces its frozen pixel height; unlocked tiles
 *  keep their stored span. Positions are PRESERVED (gaps and all) — only a tile
 *  a frozen-size locked span would overlap is pushed straight down to clear it.
 *  This is what makes locking inert: with nothing overflowing, every tile stays
 *  exactly where the operator left it (the old magnet-up closed gaps, which
 *  made the whole workspace jump the instant a panel was locked). */
function renderAtRowHeight(
  tiles: DeriveTile[],
  rowHeight: number,
  margin: number,
  lockedPxByUid: Map<string, number>,
): { placements: RenderPlacement[]; maxBottomPx: number } {
  const items: Layout = tiles.map((t) => ({
    i: t.uid,
    x: t.x,
    y: t.y,
    w: t.w,
    h: t.locked
      ? lockedSpanRows(lockedPxByUid.get(t.uid) ?? 0, rowHeight, margin)
      : t.h,
    static: t.locked,
    moved: false,
  }));
  const compacted = resolveOverlapsDownOnly(items);
  let maxBottomPx = 0;
  for (const it of compacted) {
    maxBottomPx = Math.max(
      maxBottomPx,
      tileBottomPx(it.y, it.h, rowHeight, margin),
    );
  }
  return {
    placements: compacted.map((it) => ({
      uid: it.i,
      x: it.x,
      y: it.y,
      w: it.w,
      h: it.h,
    })),
    maxBottomPx,
  };
}

/**
 * Compute the workspace render layout. With nothing locked this reproduces the
 * legacy uniform shrink-to-fit exactly. When tiles are locked, each is held at
 * its frozen pixel height (captured at lock time) while the rowHeight — and so
 * the unlocked tiles — shrink to keep the whole layout inside the viewport.
 */
export function deriveWorkspaceLayout(
  tiles: DeriveTile[],
  opts: DeriveOptions,
): DerivedWorkspaceLayout {
  const layoutRows = maxBottomOf(tiles);
  const anyLocked = tiles.some((t) => t.locked);

  if (!anyLocked || opts.containerHeight <= 0) {
    return uniformResult(tiles, layoutRows, opts);
  }

  const {
    authoredRowHeightPx: R,
    gridMarginPx: m,
    minRowHeightPx: minR,
    containerHeight: C,
  } = opts;

  // Use the same row margin the no-lock path would, so locking doesn't shift
  // the gap (and thus the sizes). It depends only on the stored layout extent,
  // not on the rowHeight we are about to solve for, so it is stable across the
  // search.
  const baseTargetRows = Math.max(layoutRows, opts.targetRows);
  const rowMargin = uniformRowMargin(C, baseTargetRows, m, opts.rowGapShare);

  // Frozen pixel height per locked tile. A tile locked before the captured
  // field existed has no stored height, so use its height at the no-lock
  // density — that keeps locking inert for legacy layouts too.
  const baselineRowHeight = uniformRowHeight(C, baseTargetRows, rowMargin, R, minR);
  const lockedPxByUid = new Map<string, number>();
  for (const t of tiles) {
    if (!t.locked) continue;
    const px =
      t.lockedHeightPx !== undefined && t.lockedHeightPx > 0
        ? t.lockedHeightPx
        : tileHeightPx(t.h, baselineRowHeight, rowMargin);
    lockedPxByUid.set(t.uid, px);
  }

  // Pick the largest rowHeight at which the whole layout still fits, with
  // locked tiles held at their frozen pixel height. A larger rowHeight makes
  // the unlocked tiles taller (more total height), so "fits" is monotonic and
  // a binary search lands on the tightest non-overflowing value.
  //
  // The ceiling is `baselineRowHeight` — the rowHeight the NO-LOCK uniform path
  // would use right now — NOT the authored `R`. Locking must never make an
  // unlocked tile bigger than it would be with nothing locked; the feature only
  // ever shrinks unlocked tiles to make room for frozen locked ones. Capping at
  // `R` was a bug: when the layout is shorter than WORKSPACE_TARGET_ROWS the
  // no-lock path already runs below `R` (the target-row divisor shrinks it), yet
  // such a layout still "fits" at `R`, so the solver jumped rowHeight UP on lock
  // — growing every unlocked tile and opening a gap around the pinned panel.
  // Reserving a locked tile's *current* height changes nothing, so at the
  // instant of locking this resolves to the pre-lock baseline rowHeight (inert).
  const atMax = renderAtRowHeight(tiles, baselineRowHeight, rowMargin, lockedPxByUid);
  let best: { rr: number; placements: RenderPlacement[] } | null =
    atMax.maxBottomPx <= C ? { rr: baselineRowHeight, placements: atMax.placements } : null;
  if (!best) {
    let lo = minR;
    let hi = baselineRowHeight;
    for (let i = 0; i < 24; i += 1) {
      const mid = (lo + hi) / 2;
      const r = renderAtRowHeight(tiles, mid, rowMargin, lockedPxByUid);
      if (r.maxBottomPx <= C) {
        best = { rr: mid, placements: r.placements };
        lo = mid;
      } else {
        hi = mid;
      }
    }
  }

  // Even the minimum rowHeight overflows — the locked tiles alone exceed the
  // viewport. Never scroll: fall back to the uniform shrink, which lets locked
  // tiles give up their frozen size as a last resort.
  if (!best) {
    return uniformResult(tiles, layoutRows, opts);
  }

  return {
    rowHeight: best.rr,
    rowMargin,
    placements: best.placements,
    compensated: true,
    unlockedScale: 1,
  };
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

/**
 * Resolve overlaps by pushing the LOWER tile of each colliding pair straight
 * down — never pulling anything up. Static (locked) tiles are immovable
 * obstacles. Unlike a magnet-up compaction this leaves every pre-existing gap
 * intact, so a layout that already fits is returned untouched (the inertness
 * the lock relies on); only a frozen-size locked span that genuinely overlaps
 * the tile below it forces that tile down to clear the collision.
 */
function resolveOverlapsDownOnly(layout: Layout): Layout {
  const next = layout.map((item) => ({ ...item }));
  const maxPasses = Math.max(1, next.length * next.length);

  for (let pass = 0; pass < maxPasses; pass += 1) {
    const ordered = [...next].sort((a, b) => a.y - b.y || a.x - b.x);
    let moved = false;
    for (let i = 0; i < ordered.length; i += 1) {
      const upper = ordered[i]!;
      for (let j = i + 1; j < ordered.length; j += 1) {
        const lower = ordered[j]!;
        if (!overlaps(upper, lower)) continue;
        // `lower` is the later one in (y, x) order — push it below `upper`.
        // A locked tile is never moved; the overlap (rare: a locked tile sitting
        // under a movable one) is left for Tidy / the operator to resolve.
        if (lower.static) continue;
        const y = upper.y + upper.h;
        if (lower.y < y) {
          lower.y = y;
          moved = true;
        }
      }
    }
    if (!moved) break;
  }
  return next;
}

function overlaps(a: LayoutItem, b: LayoutItem): boolean {
  if (a.i === b.i) return false;
  if (a.x + a.w <= b.x) return false;
  if (a.x >= b.x + b.w) return false;
  if (a.y + a.h <= b.y) return false;
  if (a.y >= b.y + b.h) return false;
  return true;
}
