// SPDX-License-Identifier: GPL-2.0-or-later

import type { Compactor, Layout, LayoutItem } from 'react-grid-layout';

export interface WorkspaceDragStartSnapshot {
  item: LayoutItem;
  layout: Layout;
}

// Keep horizontal placement operator-directed, but make vertical placement
// magnetic: when a tile is dragged into an occupied slot, the displaced tile
// should swap into the dragged tile's old slot and the stack should close up.
export const WORKSPACE_DRAG_COMPACTOR = createWorkspaceDragCompactor();

export function createWorkspaceDragCompactor(
  getDragStart?: () => WorkspaceDragStartSnapshot | null,
): Compactor {
  // Per-drag hysteresis state. `engaged` holds the ids of neighbours currently
  // committed to a swap; it is rebuilt whenever the drag snapshot identity
  // changes (a new gesture) and dropped when the drag ends (snapshot null).
  // Carrying it across frames is what gives the swap a deadband — without it
  // the swap re-derives from scratch every frame and flickers at the boundary.
  let session: {
    dragStart: WorkspaceDragStartSnapshot;
    engaged: Set<string>;
  } | null = null;
  return {
    type: null,
    allowOverlap: false,
    compact: (layout, cols) => {
      const dragStart = getDragStart?.() ?? null;
      if (!dragStart) {
        session = null;
      } else if (!session || session.dragStart !== dragStart) {
        session = { dragStart, engaged: new Set() };
      }
      return compactDragMagnetic(
        layout,
        cols,
        dragStart,
        session?.engaged ?? null,
      );
    },
  };
}

export const WORKSPACE_RESIZE_COMPACTOR: Compactor = {
  type: null,
  allowOverlap: false,
  compact: compactResizePushDown,
};

function compactDragMagnetic(
  layout: Layout,
  cols: number,
  dragStart: WorkspaceDragStartSnapshot | null,
  engaged: Set<string> | null,
): Layout {
  const priorityId = layout.find((item) => item.moved)?.i;
  if (!priorityId || !dragStart) {
    return compactPushDownWithPriority(layout, priorityId).map((item) => ({
      ...item,
      moved: false,
    }));
  }

  const dragLayout = layoutFromDragStart(layout, priorityId, dragStart);
  const swapped = swapDisplacedIntoPreviousSlot(
    dragLayout,
    cols,
    priorityId,
    dragStart,
    engaged,
  );
  // During a live drag keep the dragged tile pinned to the pointer cell, even
  // on frames where no swap fires. Toggling preservePriority with the swap
  // (the old behaviour) let compactMagnetUp yank the tile upward the instant
  // its overlap dipped below a collision; RGL then dragged it back toward the
  // pointer next frame — the preserve-toggle 2-cycle that read as a "quick
  // back and forth" at the swap boundary. A live session pins unconditionally.
  const preservePriority = engaged ? true : swapped.displaced;
  return compactMagnetUp(
    swapped.layout,
    priorityId,
    preservePriority,
  ).map((item) => ({
    ...item,
    moved: false,
  }));
}

export function autoFitDroppedPanel(
  layout: Layout,
  cols: number,
  previous?: LayoutItem | WorkspaceDragStartSnapshot | null,
): Layout {
  const dragStart = normalizeDragStart(previous);
  const previousItem = dragStart?.item ?? null;
  const dropped = previousItem
    ? layout.find((item) => item.i === previousItem.i)
    : findDroppedItem(layout);
  if (!dropped) return clearMovedFlags(cloneLayout(layout));

  const dragLayout = dragStart?.layout.length
    ? layoutFromDragStart(layout, dropped.i, dragStart)
    : layout;
  const swapped = swapDisplacedIntoPreviousSlot(
    dragLayout,
    cols,
    dropped.i,
    dragStart?.layout.length ? dragStart : null,
    null,
  );
  const base = swapped.layout;
  const target = base.find((item) => item.i === dropped.i);
  if (!target) return clearMovedFlags(base);

  const minW = boundedMin(target.minW, 1, cols);
  const maxW = boundedMax(target.maxW, minW, cols);
  const minH = boundedMin(target.minH, 1, Number.MAX_SAFE_INTEGER);
  const maxH = boundedMax(target.maxH, minH, Number.MAX_SAFE_INTEGER);
  const startW = clamp(target.w, minW, maxW);
  const startH = clamp(target.h, minH, maxH);

  const footprintX = clamp(target.x, 0, Math.max(0, cols - startW));
  const footprintY = Math.max(0, Math.round(target.y));
  const footprintRight = Math.min(cols, footprintX + startW);
  const footprintBottom = footprintY + startH;

  // Fast path — full size at the drop origin. The search below ranks
  // candidates by originDistance FIRST (|x−footprintX|+|y−footprintY|), and
  // only the origin cell scores 0, so no farther placement can ever beat an
  // origin one. At the origin the largest area is the panel's full
  // (startW × startH) — exactly what the nested loop tries first. So when full
  // size fits at the origin (the overwhelmingly common drag case, since
  // resolveAnchorCollisions pushes movable neighbours aside), it is the global
  // optimum and the whole O(footprintArea × maxW × maxH) sweep is redundant.
  // Skipping it here is what stops a large panel (e.g. the panadapter/hero
  // tile, ~18×38) from freezing the main thread for seconds on drop.
  const originTrial = cloneLayout(base);
  const originTarget = originTrial.find((item) => item.i === target.i);
  if (originTarget) {
    originTarget.x = footprintX;
    originTarget.y = footprintY;
    originTarget.w = startW;
    originTarget.h = startH;
    const originResolved = resolveAnchorCollisions(
      originTrial,
      target.i,
      cols,
      previousItem,
    );
    if (originResolved) {
      return clearMovedFlags(
        compactMagnetUp(originResolved.layout, target.i, swapped.displaced),
      );
    }
  }

  let best: {
    layout: LayoutItem[];
    area: number;
    originDistance: number;
    movement: number;
  } | null = null;

  for (let y = footprintY; y <= footprintBottom - minH; y += 1) {
    // An origin candidate (originDistance 0) is unbeatable by anything farther
    // out, so once one is found there is no need to keep sweeping the
    // footprint — this caps the fallback search at the origin cell.
    if (best && best.originDistance === 0) break;
    const maxCandidateH = Math.min(maxH, footprintBottom - y);
    for (let x = footprintX; x <= footprintRight - minW; x += 1) {
      if (best && best.originDistance === 0) break;
      const maxCandidateW = Math.min(maxW, footprintRight - x);
      for (let w = maxCandidateW; w >= minW; w -= 1) {
        for (let h = maxCandidateH; h >= minH; h -= 1) {
          const trial = cloneLayout(base);
          const trialTarget = trial.find((item) => item.i === target.i);
          if (!trialTarget) continue;
          trialTarget.x = x;
          trialTarget.y = y;
          trialTarget.w = w;
          trialTarget.h = h;

          const resolved = resolveAnchorCollisions(
            trial,
            target.i,
            cols,
            previousItem,
          );
          if (!resolved) continue;

          const area = w * h;
          const originDistance =
            Math.abs(x - footprintX) + Math.abs(y - footprintY);
          const movement = placementMovement(base, resolved.layout, target.i);
          if (
            !best ||
            originDistance < best.originDistance ||
            (originDistance === best.originDistance && area > best.area) ||
            (originDistance === best.originDistance &&
              area === best.area &&
              movement < best.movement)
          ) {
            best = { layout: resolved.layout, area, originDistance, movement };
          }
        }
      }
    }
  }

  if (best) {
    return clearMovedFlags(
      compactMagnetUp(best.layout, target.i, swapped.displaced),
    );
  }

  const fallback = cloneLayout(base);
  const fallbackTarget = fallback.find((item) => item.i === target.i);
  if (!fallbackTarget) return clearMovedFlags(fallback);
  fallbackTarget.w = minW;
  fallbackTarget.h = minH;
  fallbackTarget.x = clamp(fallbackTarget.x, 0, Math.max(0, cols - minW));
  fallbackTarget.y = Math.max(0, fallbackTarget.y);
  return clearMovedFlags(compactMagnetUp(fallback, target.i, swapped.displaced));
}

function compactResizePushDown(layout: Layout): Layout {
  return compactPushDownWithPriority(layout);
}

function swapDisplacedIntoPreviousSlot(
  layout: Layout,
  cols: number,
  priorityId: string | undefined,
  dragStart: WorkspaceDragStartSnapshot | null,
  engaged: Set<string> | null,
): { layout: Layout; displaced: boolean } {
  const next = cloneLayout(layout);
  if (!priorityId || !dragStart || dragStart.item.i !== priorityId) {
    return { layout: next, displaced: false };
  }

  const anchor = next.find((item) => item.i === priorityId);
  if (!anchor) return { layout: next, displaced: false };

  const previousById = new Map(dragStart.layout.map((item) => [item.i, item]));
  const originalOrder = new Map(
    dragStart.layout.map((item, index) => [item.i, index]),
  );
  const candidates = next
    .filter((item) => item.i !== priorityId)
    .map((item) => ({ item, previous: previousById.get(item.i) }))
    .filter(
      (
        candidate,
      ): candidate is { item: LayoutItem; previous: LayoutItem } => {
        if (candidate.previous === undefined) return false;
        // Live drag: gate engagement through the per-drag hysteresis set so a
        // neighbour holds its swapped slot across the touch boundary instead of
        // flickering. Drop / one-shot compaction (engaged === null) keeps the
        // immediate collision test — there is no next frame to flicker into.
        if (engaged) {
          return updateSwapEngagement(
            candidate.item.i,
            candidate.previous,
            anchor,
            engaged,
          );
        }
        return (
          placementChanged(candidate.item, candidate.previous) ||
          collides(candidate.previous, anchor) ||
          collides(candidate.item, anchor)
        );
      },
    )
    .sort((a, b) =>
      compareDisplacedCandidates(a, b, anchor, originalOrder),
    );

  for (const { item, previous } of candidates) {
    const slot = nearestOpenSlot(
      next,
      item,
      anchor,
      cols,
      previous,
      dragStart.item,
    );
    if (slot === null) continue;
    item.x = slot.x;
    item.y = slot.y;
    return { layout: next, displaced: true };
  }

  return { layout: next, displaced: false };
}

function layoutFromDragStart(
  layout: Layout,
  priorityId: string,
  dragStart: WorkspaceDragStartSnapshot,
): Layout {
  const current = new Map(layout.map((item) => [item.i, item]));
  const anchor = current.get(priorityId);
  const next: LayoutItem[] = dragStart.layout.map((item) => {
    if (item.i !== priorityId) return { ...item, moved: false };
    return { ...(anchor ?? item), moved: true };
  });

  for (const item of layout) {
    if (!dragStart.layout.some((startItem) => startItem.i === item.i)) {
      next.push({ ...item });
    }
  }

  return next;
}

function compactMagnetUp(
  layout: Layout,
  priorityId?: string,
  preservePriority = false,
): Layout {
  const next = cloneLayout(layout);
  const originalOrder = new Map(next.map((item, index) => [item.i, index]));
  const priority = preservePriority && priorityId
    ? next.find((item) => item.i === priorityId)
    : undefined;
  const placed: LayoutItem[] = priority ? [priority] : [];
  const ordered = next
    .filter((item) => item.i !== priority?.i)
    .sort((a, b) => compareItems(a, b, originalOrder));

  for (const item of ordered) {
    if (!item.static) {
      item.y = Math.max(0, item.y);
      moveBelowCollisions(item, placed);
      while (item.y > 0) {
        item.y -= 1;
        const collision = firstCollision(placed, item);
        if (collision) {
          item.y = collision.y + collision.h;
          break;
        }
      }
      moveBelowCollisions(item, placed);
    }
    placed.push(item);
  }

  return next;
}

function moveBelowCollisions(item: LayoutItem, placed: LayoutItem[]) {
  let collision = firstCollision(placed, item);
  while (collision) {
    item.y = collision.y + collision.h;
    collision = firstCollision(placed, item);
  }
}

function firstCollision(
  layout: LayoutItem[],
  item: LayoutItem,
): LayoutItem | undefined {
  return layout.find((other) => collides(other, item));
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

function placementChanged(item: LayoutItem, previous: LayoutItem): boolean {
  return (
    item.x !== previous.x ||
    item.y !== previous.y ||
    item.w !== previous.w ||
    item.h !== previous.h
  );
}

function compareDisplacedCandidates(
  a: { item: LayoutItem; previous: LayoutItem },
  b: { item: LayoutItem; previous: LayoutItem },
  anchor: LayoutItem,
  originalOrder: Map<string, number>,
) {
  const aOldSlotHit = collides(a.previous, anchor);
  const bOldSlotHit = collides(b.previous, anchor);
  if (aOldSlotHit !== bOldSlotHit) return aOldSlotHit ? -1 : 1;

  const aCurrentHit = collides(a.item, anchor);
  const bCurrentHit = collides(b.item, anchor);
  if (aCurrentHit !== bCurrentHit) return aCurrentHit ? -1 : 1;

  const aDistance = rectDistance(a.previous, anchor);
  const bDistance = rectDistance(b.previous, anchor);
  return (
    aDistance - bDistance ||
    (originalOrder.get(a.item.i) ?? 0) - (originalOrder.get(b.item.i) ?? 0)
  );
}

function rectDistance(a: LayoutItem, b: LayoutItem): number {
  return Math.abs(a.x - b.x) + Math.abs(a.y - b.y);
}

function compactPushDownWithPriority(
  layout: Layout,
  priorityId?: string,
): Layout {
  const next = layout.map((item) => ({ ...item }));
  const originalOrder = new Map(next.map((item, index) => [item.i, index]));
  const maxPasses = Math.max(1, next.length * next.length);

  for (let pass = 0; pass < maxPasses; pass += 1) {
    let moved = false;
    const ordered = [...next].sort((a, b) =>
      compareItems(a, b, originalOrder, priorityId),
    );

    for (let i = 0; i < ordered.length; i += 1) {
      const anchor = ordered[i]!;
      for (let j = i + 1; j < ordered.length; j += 1) {
        const candidate = ordered[j]!;
        if (!collides(anchor, candidate)) continue;

        if (candidate.static) {
          if (!anchor.static) {
            const y = candidate.y + candidate.h;
            if (anchor.y < y) {
              anchor.y = y;
              moved = true;
            }
          }
          continue;
        }

        const y = anchor.y + anchor.h;
        if (candidate.y < y) {
          candidate.y = y;
          moved = true;
        }
      }
    }

    if (!moved) break;
  }

  return next;
}

function resolveAnchorCollisions(
  layout: Layout,
  anchorId: string,
  cols: number,
  preferredSlot?: LayoutItem | null,
): { layout: LayoutItem[] } | null {
  const next = cloneLayout(layout);
  const originalPlacement = new Map(
    next.map((item) => [item.i, { x: item.x, y: item.y }]),
  );
  const maxPasses = Math.max(1, next.length * next.length);

  for (let pass = 0; pass < maxPasses; pass += 1) {
    const anchor = next.find((item) => item.i === anchorId);
    if (!anchor) return null;

    const collisions = next
      .filter((item) => item.i !== anchorId && collides(anchor, item));

    if (collisions.some((item) => item.static)) return null;

    const movableCollisions = collisions
      .sort((a, b) => Math.abs(a.x - anchor.x) - Math.abs(b.x - anchor.x));

    if (movableCollisions.length === 0) {
      return anyCollision(next) ? null : { layout: next };
    }

    let moved = false;
    for (const item of movableCollisions) {
      const slot = nearestOpenSlot(
        next,
        item,
        anchor,
        cols,
        originalPlacement.get(item.i) ?? item,
        preferredSlot,
      );
      if (slot === null) return null;
      if (slot.x !== item.x || slot.y !== item.y) {
        item.x = slot.x;
        item.y = slot.y;
        moved = true;
      }
    }

    if (!moved) return null;
  }

  return anyCollision(next) ? null : { layout: next };
}

function nearestOpenSlot(
  layout: LayoutItem[],
  item: LayoutItem,
  anchor: LayoutItem,
  cols: number,
  originalPlacement: Pick<LayoutItem, 'x' | 'y'>,
  preferredSlot?: Pick<LayoutItem, 'x' | 'y'> | null,
): Pick<LayoutItem, 'x' | 'y'> | null {
  const maxX = cols - item.w;
  if (maxX < 0) return null;

  const layoutBottom = layout.reduce(
    (bottom, candidate) => Math.max(bottom, candidate.y + candidate.h),
    0,
  );
  const tallestItem = layout.reduce(
    (height, candidate) => Math.max(height, candidate.h),
    item.h,
  );
  const maxY = layoutBottom + tallestItem * Math.max(1, layout.length);

  let best: {
    x: number;
    y: number;
    preferredDistance: number;
    distance: number;
  } | null = null;

  for (let y = 0; y <= maxY; y += 1) {
    for (let x = 0; x <= maxX; x += 1) {
      const candidate = { ...item, x, y };
      if (collides(candidate, anchor)) continue;
      if (
        layout.some((other) => {
          if (other.i === item.i || other.i === anchor.i) return false;
          return collides(candidate, other);
        })
      ) {
        continue;
      }

      const preferredDistance = preferredSlot
        ? Math.abs(x - preferredSlot.x) + Math.abs(y - preferredSlot.y)
        : 0;
      const distance =
        Math.abs(x - originalPlacement.x) + Math.abs(y - originalPlacement.y);
      if (
        !best ||
        preferredDistance < best.preferredDistance ||
        (preferredDistance === best.preferredDistance &&
          distance < best.distance) ||
        (preferredDistance === best.preferredDistance &&
          distance === best.distance &&
          y < best.y) ||
        (preferredDistance === best.preferredDistance &&
          distance === best.distance &&
          y === best.y &&
          x < best.x)
      ) {
        best = { x, y, preferredDistance, distance };
      }
    }
  }

  return best ? { x: best.x, y: best.y } : null;
}

function findDroppedItem(layout: Layout): LayoutItem | undefined {
  const moved = layout.filter((item) => item.moved);
  return moved[moved.length - 1];
}

function compareItems(
  a: LayoutItem,
  b: LayoutItem,
  originalOrder: Map<string, number>,
  priorityId?: string,
) {
  if (priorityId) {
    if (a.i === priorityId && b.i !== priorityId) return -1;
    if (b.i === priorityId && a.i !== priorityId) return 1;
  }
  return (
    a.y - b.y ||
    a.x - b.x ||
    (originalOrder.get(a.i) ?? 0) - (originalOrder.get(b.i) ?? 0)
  );
}

function cloneLayout(layout: Layout): LayoutItem[] {
  return layout.map((item) => ({ ...item }));
}

function clearMovedFlags(layout: Layout): LayoutItem[] {
  return layout.map((item) => ({ ...item, moved: false }));
}

function placementMovement(
  before: Layout,
  after: Layout,
  excludeId: string,
): number {
  const beforePlacement = new Map(
    before.map((item) => [item.i, { x: item.x, y: item.y }]),
  );
  return after.reduce((sum, item) => {
    if (item.i === excludeId) return sum;
    const previous = beforePlacement.get(item.i);
    if (!previous) return sum;
    return (
      sum +
      Math.abs(item.x - previous.x) +
      Math.abs(item.y - previous.y)
    );
  }, 0);
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

// Hysteresis thresholds for the live magnetic swap (grid rows). A neighbour
// engages once the dragged tile overlaps it by at least ENGAGE rows, and only
// disengages once they are separated by more than RELEASE rows (a strict gap).
// The band between them — the touch boundary, where RGL's grid-rounding jitters
// the dragged tile by a single row — is a deadband: a neighbour that is already
// committed stays committed across it, so the swap can't flicker on and off.
const SWAP_ENGAGE_ROWS = 1;
const SWAP_RELEASE_ROWS = 0;

// Signed vertical overlap between the dragged anchor and a neighbour, in rows:
// positive when they overlap, zero when edges touch, negative (a gap) when they
// are apart. Returns -Infinity when their columns don't overlap at all, so a
// neighbour off to the side never engages and any engaged one releases at once.
function verticalOverlapRows(anchor: LayoutItem, neighbor: LayoutItem): number {
  if (anchor.x + anchor.w <= neighbor.x || anchor.x >= neighbor.x + neighbor.w) {
    return Number.NEGATIVE_INFINITY;
  }
  return (
    Math.min(anchor.y + anchor.h, neighbor.y + neighbor.h) -
    Math.max(anchor.y, neighbor.y)
  );
}

// Advance the per-drag engagement set for one neighbour and report whether it
// is currently committed to a swap. Engage on real overlap, release only on a
// clear gap; the touch boundary in between is sticky.
function updateSwapEngagement(
  id: string,
  previous: LayoutItem,
  anchor: LayoutItem,
  engaged: Set<string>,
): boolean {
  const overlap = verticalOverlapRows(anchor, previous);
  if (engaged.has(id)) {
    if (overlap < SWAP_RELEASE_ROWS) engaged.delete(id);
  } else if (overlap >= SWAP_ENGAGE_ROWS) {
    engaged.add(id);
  }
  return engaged.has(id);
}

function boundedMin(value: number | undefined, fallback: number, ceiling: number) {
  const min = Number.isFinite(value) ? value! : fallback;
  return clamp(Math.round(min), fallback, ceiling);
}

function boundedMax(value: number | undefined, floor: number, ceiling: number) {
  const max = Number.isFinite(value) ? value! : ceiling;
  return Math.max(floor, clamp(Math.round(max), floor, ceiling));
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, Math.round(value)));
}
