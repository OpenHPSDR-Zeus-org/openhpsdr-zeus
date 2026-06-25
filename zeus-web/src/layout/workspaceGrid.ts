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
//         SWAP and nothing else moves (an in-place exchange — never pushes a
//         tile below the fold).
//       • lands overlapping anything else (or a locked tile) → it STAYS exactly
//         where dropped, OVERLAPPING what it covers. Nothing relocates: the old
//         relocate-to-free-slot searched downward and shoved the tile off the
//         bottom (bringing the scrollbar back). The operator clears space by
//         moving/shrinking the covered panel.
//   - On RESIZE STOP (resolveResizeOverlaps): the resized tile keeps its new
//     size and OVERLAPS whatever it now covers — neighbours never move. Growing
//     a panel must not displace another (that used to shove neighbours down past
//     the fold or off the monitor, where they were then pruned). To clear the
//     space, the operator shrinks/moves the covered panel. Only a LOCKED tile is
//     protected: the resized tile is clamped back out of it instead.
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

  // Otherwise the dropped tile STAYS exactly where the operator dropped it,
  // OVERLAPPING whatever it now covers (including a locked tile — only the
  // locked tile's own position is protected, and it never moves because it is
  // not the dragged tile). It used to relocate to the nearest free slot, but
  // that search ran downward and shoved the tile below the fold, bringing the
  // scrollbar back. Overlap is allowed; nothing moves but the tile in hand. To
  // clear the space, the operator moves or shrinks the covered panel.
  return next;
}

/**
 * Resolve a resize. OVERLAP IS ALLOWED: the resized tile keeps its new geometry
 * and grows OVER its neighbours, which stay exactly where they are. Resizing a
 * panel larger must never displace another panel — pushing a neighbour to a
 * "free slot" used to shove it down past the fold (bringing the scrollbar back)
 * or off the monitor entirely (where it would then be pruned and lost). The
 * operator covers a panel deliberately; to clear the space they shrink or move
 * the covered panel themselves. This holds for every panel, the panadapter
 * included.
 *
 * The one thing still enforced: a resize cannot cover a LOCKED (pinned) tile.
 * Rather than move the locked tile, clamp the resized tile back out of it (trim
 * height first — the common "grew downward into a locked tile" — then width).
 * Nothing is ever relocated, so there is no cascade and nothing leaves the field.
 */
export function resolveResizeOverlaps(
  layout: Layout,
  resizedId: string,
  _cols: number,
): Layout {
  const next = clearMovedFlags(cloneLayout(layout));
  const resized = next.find((item) => item.i === resizedId);
  if (!resized) return next;

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
