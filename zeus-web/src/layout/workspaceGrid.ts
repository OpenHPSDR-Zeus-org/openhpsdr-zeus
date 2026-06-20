// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Workspace grid placement — FIXED-LATTICE model.
//
// The workspace is a free grid, not a packing grid. A panel sits exactly where
// the operator drops it and STAYS there; moving one panel never reflows the
// others. This is a deliberate replacement of the old magnetic-compaction
// engine, whose global vertical re-pack was the root cause of the "I moved one
// and they all moved" reports — compaction inherently cascades, so it could
// never be tuned away.
//
// The rules, end to end:
//   - During a live drag/resize NOTHING reflows. The dragged tile floats over
//     its neighbours (transient overlap is fine — RGL lifts it on top) and the
//     neighbours hold their cells. The live compactors below are therefore the
//     identity (they only strip RGL's transient `moved` flag).
//   - On DROP (placeDroppedPanel): the dragged tile lands where it was dropped.
//       • lands on empty space → it just stays there (gaps are allowed).
//       • lands squarely on ONE movable same-footprint-ish neighbour → the two
//         SWAP and nothing else moves.
//       • lands overlapping anything else (or a locked tile) → only the dragged
//         tile relocates, to the nearest free slot to the drop point. Every
//         other tile is left untouched.
//   - On RESIZE STOP (resolveResizeOverlaps): the resized tile keeps its new
//     size; only the neighbours it now overlaps hop to their nearest free slot.
//     One pass, each to a globally-free cell, so there is no cascade.
//   - Tidy (tidyWorkspacePlacements): an explicit, operator-invoked pack that
//     closes vertical gaps. Horizontal placement stays operator-directed (x is
//     preserved); locked tiles never move. This is the ONLY path that packs.
//
// Locked (static) tiles are inert everywhere: they are never relocated, and the
// free-slot search treats them as immovable obstacles.

import type { Compactor, Layout, LayoutItem } from 'react-grid-layout';

export interface WorkspaceDragStartSnapshot {
  item: LayoutItem;
  layout: Layout;
}

// Live-interaction compactor: the identity. It never moves a tile — it only
// clears RGL's transient `moved` flag so the echoed layout is clean. Both drag
// and resize use this; all real placement happens on drop / resize-stop.
//
// allowOverlap:true lets the dragged tile float over its neighbours during the
// gesture (resolved on drop); preventCollision is intentionally omitted (falsy)
// so RGL does not try to shove neighbours aside while dragging.
const FREE_COMPACTOR: Compactor = {
  type: null,
  allowOverlap: true,
  compact: (layout) => clearMovedFlags(layout),
};

export const WORKSPACE_DRAG_COMPACTOR = FREE_COMPACTOR;
export const WORKSPACE_RESIZE_COMPACTOR = FREE_COMPACTOR;

// Kept for call-site compatibility with the old magnetic engine. The drag
// snapshot is no longer needed for live compaction (nothing reflows mid-drag);
// it is captured by FlexWorkspace and handed to placeDroppedPanel on drop.
export function createWorkspaceDragCompactor(
  _getDragStart?: () => WorkspaceDragStartSnapshot | null,
): Compactor {
  return FREE_COMPACTOR;
}

// Fraction of the smaller tile's area that must be covered for a drop to count
// as "squarely on" a neighbour and trigger a swap rather than a relocate. Below
// this, a glancing overlap just relocates the dragged tile to free space.
const SWAP_OVERLAP_FRACTION = 0.5;

/**
 * Resolve a dropped panel into the fixed lattice. The dragged tile lands where
 * it was dropped; if that overlaps a single movable neighbour squarely the two
 * swap, otherwise the dragged tile (and ONLY the dragged tile) relocates to the
 * nearest free slot. No other tile is ever moved.
 *
 * `previous` is the drag-start snapshot (item = the tile's original placement,
 * layout = the full layout at drag start). It is what lets a swap put the
 * displaced neighbour into the dragged tile's vacated slot.
 *
 * Named `autoFitDroppedPanel` for call-site continuity with the old engine.
 */
export function autoFitDroppedPanel(
  layout: Layout,
  cols: number,
  previous?: LayoutItem | WorkspaceDragStartSnapshot | null,
): Layout {
  const dragStart = normalizeDragStart(previous);
  const next = clearMovedFlags(cloneLayout(layout));
  const draggedId = dragStart?.item.i ?? findDroppedItem(layout)?.i;
  if (!draggedId) return next;
  const dragged = next.find((item) => item.i === draggedId);
  if (!dragged) return next;

  // Snap the drop point into the grid (clamp x; floor y to a row).
  const maxX = Math.max(0, cols - dragged.w);
  const dropX = clampInt(dragged.x, 0, maxX);
  const dropY = Math.max(0, Math.round(dragged.y));
  dragged.x = dropX;
  dragged.y = dropY;

  const others = next.filter((item) => item.i !== draggedId);
  const colliders = others.filter((item) => collides(dragged, item));

  // Dropped onto empty space — it stays exactly here; nothing else moves.
  if (colliders.length === 0) return next;

  // Squarely onto exactly one movable neighbour → swap, if the swap leaves the
  // layout collision-free (a same-footprint swap always does; an odd-sized one
  // only when it happens to fit, otherwise we fall through to relocate).
  const origin = dragStart?.item ?? null;
  if (colliders.length === 1 && origin) {
    const target = colliders[0]!;
    if (!target.static && overlapIsSquare(dragged, target)) {
      const tx = target.x;
      const ty = target.y;
      dragged.x = tx;
      dragged.y = ty;
      target.x = clampInt(origin.x, 0, Math.max(0, cols - target.w));
      target.y = Math.max(0, Math.round(origin.y));
      if (!anyCollision(next)) return next;
      // Swap would overlap a third tile — undo and relocate instead.
      dragged.x = dropX;
      dragged.y = dropY;
      target.x = tx;
      target.y = ty;
    }
  }

  // Otherwise relocate ONLY the dragged tile to the nearest free slot to the
  // drop point. Neighbours (including locked tiles) are obstacles, never moved.
  const slot = nearestFreeSlot(next, dragged, cols, dropX, dropY);
  if (slot) {
    dragged.x = slot.x;
    dragged.y = slot.y;
  } else {
    // Pathological — drop below everything, which is always free.
    dragged.x = dropX;
    dragged.y = others.reduce((b, it) => Math.max(b, it.y + it.h), 0);
  }
  return next;
}

/**
 * Resolve overlaps created by a resize. The resized tile keeps its new
 * geometry; each movable neighbour it now overlaps relocates to its nearest
 * free slot (single pass — every relocation targets a globally-free cell, so no
 * cascade). Locked neighbours are left in place (a resize into a locked tile is
 * a rare edge the operator can clear with Tidy).
 */
export function resolveResizeOverlaps(
  layout: Layout,
  resizedId: string,
  cols: number,
): Layout {
  const next = clearMovedFlags(cloneLayout(layout));
  const resized = next.find((item) => item.i === resizedId);
  if (!resized) return next;

  const overlapped = next
    .filter((item) => item.i !== resizedId && !item.static)
    .filter((item) => collides(resized, item))
    .sort((a, b) => a.y - b.y || a.x - b.x);

  for (const item of overlapped) {
    // A prior relocation may already have cleared this one.
    if (!collides(resized, item)) continue;
    const slot = nearestFreeSlot(next, item, cols, item.x, item.y);
    if (slot) {
      item.x = slot.x;
      item.y = slot.y;
    }
  }

  // A locked neighbour can't be relocated — clamp the resized tile so a resize
  // never leaves a panel sitting on top of a locked one. Trim height first (the
  // common "grew downward into a locked tile"), then width.
  for (const blocker of next) {
    if (blocker.i === resizedId || !blocker.static) continue;
    if (!collides(resized, blocker)) continue;
    if (resized.y < blocker.y) {
      resized.h = Math.max(1, blocker.y - resized.y);
    } else if (resized.x < blocker.x) {
      resized.w = Math.max(1, blocker.x - resized.x);
    }
  }
  return next;
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

function nearestFreeSlot(
  layout: LayoutItem[],
  item: LayoutItem,
  cols: number,
  prefX: number,
  prefY: number,
): { x: number; y: number } | null {
  const maxX = cols - item.w;
  if (maxX < 0) return null;
  const others = layout.filter((other) => other.i !== item.i);
  const layoutBottom = layout.reduce(
    (bottom, other) => Math.max(bottom, other.y + other.h),
    0,
  );
  // One full tile-height of slack below the stack guarantees a free row exists.
  const maxY = layoutBottom + item.h;

  let best: { x: number; y: number; dist: number } | null = null;
  for (let y = 0; y <= maxY; y += 1) {
    for (let x = 0; x <= maxX; x += 1) {
      const candidate = { ...item, x, y };
      if (others.some((other) => collides(candidate, other))) continue;
      const dist = Math.abs(x - prefX) + Math.abs(y - prefY);
      if (
        !best ||
        dist < best.dist ||
        (dist === best.dist && (y < best.y || (y === best.y && x < best.x)))
      ) {
        best = { x, y, dist };
      }
      // Drop point itself is free — nothing can beat distance 0.
      if (dist === 0) return { x, y };
    }
  }
  return best ? { x: best.x, y: best.y } : null;
}

function overlapIsSquare(a: LayoutItem, b: LayoutItem): boolean {
  const overlapW = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x);
  const overlapH = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
  if (overlapW <= 0 || overlapH <= 0) return false;
  const overlapArea = overlapW * overlapH;
  const minArea = Math.min(a.w * a.h, b.w * b.h);
  return minArea > 0 && overlapArea >= minArea * SWAP_OVERLAP_FRACTION;
}

function normalizeDragStart(
  previous: LayoutItem | WorkspaceDragStartSnapshot | null | undefined,
): WorkspaceDragStartSnapshot | null {
  if (!previous) return null;
  if ('item' in previous && 'layout' in previous) {
    return {
      item: { ...previous.item },
      layout: cloneLayout(previous.layout),
    };
  }
  return { item: { ...previous }, layout: [] };
}

function findDroppedItem(layout: Layout): LayoutItem | undefined {
  const moved = layout.filter((item) => item.moved);
  return moved[moved.length - 1];
}

function cloneLayout(layout: Layout): LayoutItem[] {
  return layout.map((item) => ({ ...item }));
}

function clearMovedFlags(layout: Layout): LayoutItem[] {
  return layout.map((item) => ({ ...item, moved: false }));
}

function anyCollision(layout: Layout): boolean {
  for (let i = 0; i < layout.length; i += 1) {
    const a = layout[i]!;
    for (let j = i + 1; j < layout.length; j += 1) {
      if (collides(a, layout[j]!)) return true;
    }
  }
  return false;
}

function collides(a: LayoutItem, b: LayoutItem) {
  if (a.i === b.i) return false;
  if (a.x + a.w <= b.x) return false;
  if (a.x >= b.x + b.w) return false;
  if (a.y + a.h <= b.y) return false;
  if (a.y >= b.y + b.h) return false;
  return true;
}

function clampInt(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, Math.round(value)));
}
