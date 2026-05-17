// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Workspace tile schema for the react-grid-layout (RGL) substrate that
// replaces flexlayout-react at the desktop workspace level. One tile = one
// PanelDef instance, placed and sized in a 12-column grid. The full layout
// blob (schemaVersion + tiles[]) round-trips through /api/ui/layout via the
// existing layout-store debounced PUT path.


/** Outer-grid column count. Matches MetersPanel's inner grid so the mental
 *  model is identical at both levels. */
export const WORKSPACE_GRID_COLS = 12;
/** Fallback row height in CSS pixels, used before the workspace container's
 *  ResizeObserver fires. The live rowHeight is computed responsively in
 *  FlexWorkspace (= viewport / WORKSPACE_TARGET_ROWS) so the default layout
 *  fills the available height. */
export const WORKSPACE_ROW_HEIGHT_PX = 30;
/** Target row count the responsive rowHeight calc divides into the
 *  measured container height. Matches the total y+h of the default layout
 *  so a fresh radio fills the viewport exactly. Custom layouts taller than
 *  this row out below the fold (workspace scrolls); shorter layouts leave
 *  empty space at the bottom. */
export const WORKSPACE_TARGET_ROWS = 24;
/** Floor on the responsive rowHeight so tiles stay readable on small
 *  windows. Below this, the workspace scrolls instead of cramming. */
export const WORKSPACE_ROW_HEIGHT_MIN_PX = 18;
/** Default minW/minH for every tile. minW=1 (≈ 1/12 of the workspace
 *  width) lets short-control tiles like Mode / Tuning Step / Band shrink
 *  down to a narrow column when the operator wants to dock them tight,
 *  while RGL's `.btn-row.wrap` flex-wrap keeps the buttons spilling onto
 *  additional rows as the column narrows. */
export const WORKSPACE_TILE_MIN_W = 1;
export const WORKSPACE_TILE_MIN_H = 1;

/** A single workspace tile. The same panelId may appear on multiple tiles
 *  for multi-instance panels (just `meters` today); the per-tile uid is the
 *  identity key. */
export interface WorkspaceTile {
  /** Stable per-tile id. Survives drag/resize so React keys + RGL identity
   *  stay aligned across renders and persistence. */
  uid: string;
  /** Panel registry id (e.g. 'hero', 'meters'). */
  panelId: string;
  x: number;
  y: number;
  w: number;
  h: number;
  /** Opaque per-instance config for panels that need it. Only `meters`
   *  uses this in v1 (carries MetersPanelConfig). Forward-compatible:
   *  unknown panels' instanceConfig is preserved verbatim across save/load. */
  instanceConfig?: unknown;
}

/** Top-level workspace blob persisted to /api/ui/layout. */
export interface WorkspaceLayout {
  schemaVersion: 7;
  tiles: WorkspaceTile[];
}

export const EMPTY_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 7,
  tiles: [],
};

/** Per-panel default span used when the AddPanel modal mints a fresh tile.
 *  Sourced from the plan §1 inventory table; missing entries fall back to a
 *  generic 4×4 box. */
export const DEFAULT_TILE_SPAN: Record<string, { w: number; h: number }> = {
  filter: { w: 9, h: 2 },
  hero: { w: 9, h: 12 },
  qrz: { w: 3, h: 6 },
  logbook: { w: 3, h: 6 },
  txmeters: { w: 3, h: 6 },
  vfo: { w: 3, h: 4 },
  smeter: { w: 3, h: 2 },
  dsp: { w: 3, h: 3 },
  azimuth: { w: 3, h: 8 },
  step: { w: 3, h: 3 },
  cw: { w: 4, h: 4 },
  tx: { w: 3, h: 5 },
  ps: { w: 4, h: 5 },
  band: { w: 6, h: 2 },
  mode: { w: 4, h: 2 },
  meters: { w: 6, h: 8 },
};

const FALLBACK_SPAN = { w: 4, h: 4 };
export function defaultSpanFor(panelId: string): { w: number; h: number } {
  return DEFAULT_TILE_SPAN[panelId] ?? FALLBACK_SPAN;
}

/** Mint a unique tile uid. Uses crypto.randomUUID() when available, falls
 *  back to a Math.random suffix in old contexts (mirrors metersConfig
 *  newWidgetUid pattern for consistency). */
export function newTileUid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return `tile-${crypto.randomUUID()}`;
  }
  return `tile-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Compute a placement for a brand-new tile: use its default span and place
 *  it at y = max(existing y+h), x = 0. RGL compacts upward into any free
 *  space at render time. */
export function placeTileInGrid(
  panelId: string,
  others: WorkspaceTile[],
): { x: number; y: number; w: number; h: number } {
  const span = defaultSpanFor(panelId);
  const maxY = others.reduce((m, t) => Math.max(m, t.y + t.h), 0);
  return { x: 0, y: maxY, w: span.w, h: span.h };
}

/** Best-effort parser + validator for the opaque /api/ui/layout JSON blob.
 *  Anything malformed falls through to the empty layout — never throws. The
 *  caller is expected to substitute DEFAULT_WORKSPACE_LAYOUT when this
 *  returns the empty value. Unknown panelIds are kept rather than dropped —
 *  plugin panels register asynchronously at startup, and dropping their
 *  tiles on each layout deserialise would mean operators have to re-add
 *  plugin panels after every tab switch / page reload. The tile renderer
 *  treats an unresolved panelId as "render nothing until it shows up"
 *  (PanelTile / PanelBody both early-return null on missing def), so a
 *  tile pointing at a permanently-removed panel id is harmless. */
export function parseWorkspaceLayout(raw: unknown): WorkspaceLayout {
  if (!raw || typeof raw !== 'object') return EMPTY_WORKSPACE_LAYOUT;
  const obj = raw as Partial<WorkspaceLayout>;
  if (obj.schemaVersion !== 7) return EMPTY_WORKSPACE_LAYOUT;
  const rawTiles = Array.isArray(obj.tiles) ? obj.tiles : [];
  const tiles: WorkspaceTile[] = [];
  for (const t of rawTiles) {
    if (!t || typeof t !== 'object') continue;
    const tile = t as Partial<WorkspaceTile>;
    if (typeof tile.uid !== 'string' || tile.uid.length === 0) continue;
    if (typeof tile.panelId !== 'string') continue;
    if (
      !Number.isFinite(tile.x) ||
      !Number.isFinite(tile.y) ||
      !Number.isFinite(tile.w) ||
      !Number.isFinite(tile.h)
    )
      continue;
    tiles.push({
      uid: tile.uid,
      panelId: tile.panelId,
      x: tile.x as number,
      y: tile.y as number,
      w: tile.w as number,
      h: tile.h as number,
      ...(tile.instanceConfig !== undefined
        ? { instanceConfig: tile.instanceConfig }
        : {}),
    });
  }
  return { schemaVersion: 7, tiles };
}
