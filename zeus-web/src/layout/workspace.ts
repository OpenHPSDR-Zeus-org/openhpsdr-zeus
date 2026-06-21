// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Workspace tile schema for the react-grid-layout (RGL) substrate that
// replaces flexlayout-react at the desktop workspace level. One tile = one
// PanelDef instance, placed and sized in a 12-column grid. The full layout
// blob (schemaVersion + tiles[]) round-trips through /api/ui/layout via the
// existing layout-store debounced PUT path.


/** Outer-grid column count. The grid runs at 2× the legacy resolution
 *  (was 12 cols × 24 rows) so panels can be sized precisely to their content
 *  instead of snapping to coarse ~30 px steps — the difference between
 *  "content fits, no scrollbar" and "off by half a row". The schema-v7→v8
 *  migration in parseWorkspaceLayout scales legacy layouts up by GRID_SCALE,
 *  so the doubling is invisible to existing saved layouts. */
export const WORKSPACE_GRID_COLS = 24;
/** Authored row height in CSS pixels. FlexWorkspace uses this as the maximum
 *  live rowHeight, shrinking below it only when a dense layout must fit a
 *  shorter viewport. Halved alongside the row doubling so panels keep the
 *  same visual density as the legacy 12×24 grid. */
export const WORKSPACE_ROW_HEIGHT_PX = 15;
/** Target row count the responsive rowHeight calc divides into the
 *  measured container height when shrink-to-fit is needed. Matches the total
 *  y+h of the default layout. Custom layouts taller than this shrink
 *  proportionally into the same viewport; shorter layouts leave empty space at
 *  the bottom. */
export const WORKSPACE_TARGET_ROWS = 48;
/** Default minW/minH for every tile. minW=2 (≈ 1/12 of the workspace
 *  width at 24 cols) lets short-control tiles like Mode / Tuning Step / Band
 *  shrink down to a narrow column when the operator wants to dock them tight,
 *  while RGL's `.btn-row.wrap` flex-wrap keeps the buttons spilling onto
 *  additional rows as the column narrows. */
export const WORKSPACE_TILE_MIN_W = 2;
export const WORKSPACE_TILE_MIN_H = 2;

/** Resolution multiplier between the legacy (schema-v7) 12×24 grid and the
 *  current (schema-v8) 24×48 grid. Legacy persisted layouts have every
 *  x/y/w/h scaled by this on load so they render identically. */
export const GRID_SCALE = 2;
/** Rows beyond this are not operator-authored layouts in practice: at that
 *  density rowHeight collapses to a couple pixels and panel min-heights can
 *  no longer keep the workspace readable. Treat them as corrupted saves. */
export const WORKSPACE_OVERSIZED_LAYOUT_ROW_THRESHOLD =
  WORKSPACE_TARGET_ROWS * 5;
/** Preserve the relative vertical arrangement of a corrupted layout, but bring
 *  it back into the dense-dashboard range used by real saved workspaces. */
export const WORKSPACE_OVERSIZED_LAYOUT_NORMALIZED_ROWS = 120;

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
  /** When true, the tile is pinned to its current grid space. */
  locked?: boolean;
  /** On-screen pixel height captured at the moment the tile was locked. While
   *  locked, the tile is held at exactly this height regardless of how the
   *  workspace rows shrink (see deriveWorkspaceLayout). A raw pixel value — NOT
   *  a grid coordinate, so it is not scaled by the v7→v8 GRID_SCALE migration.
   *  Only meaningful when `locked` is true; cleared on unlock. */
  lockedHeightPx?: number;
}

/** Top-level workspace blob persisted to /api/ui/layout.
 *  v8 = current 24×48 grid. v7 = legacy 12×24 grid, migrated on load by
 *  scaling every coordinate by GRID_SCALE (see parseWorkspaceLayout). */
export interface WorkspaceLayout {
  schemaVersion: 8;
  tiles: WorkspaceTile[];
  /** When true, every tile in this workspace is pinned in place. */
  locked?: boolean;
}

export const EMPTY_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 8,
  tiles: [],
};

/** Per-panel default span used when the AddPanel modal mints a fresh tile.
 *  Sourced from the plan §1 inventory table; missing entries fall back to a
 *  generic 4×4 box. */
// All spans are in the 24×48 (schema-v8) grid — i.e. 2× the legacy values.
export const DEFAULT_TILE_SPAN: Record<string, { w: number; h: number }> = {
  filter: { w: 18, h: 4 },
  filterpresets: { w: 6, h: 10 },
  hero: { w: 18, h: 24 },
  qrz: { w: 6, h: 12 },
  logbook: { w: 6, h: 12 },
  txmeters: { w: 6, h: 12 },
  txfidelity: { w: 6, h: 22 },
  vfo: { w: 6, h: 8 },
  smeter: { w: 6, h: 4 },
  dsp: { w: 6, h: 6 },
  azimuth: { w: 6, h: 16 },
  step: { w: 6, h: 6 },
  cw: { w: 8, h: 8 },
  tx: { w: 6, h: 10 },
  ps: { w: 8, h: 10 },
  band: { w: 12, h: 4 },
  mode: { w: 8, h: 4 },
  meters: { w: 12, h: 16 },
  // Meter Group auto-fits to its widget set (see MeterGroupTileBody in
  // FlexWorkspace.tsx) — start at the smallest grid footprint so the
  // freshly-added tile shows just the empty-state hint, not 4 columns of
  // blank space waiting for widgets.
  metergroup: { w: 2, h: 12 },
  // HamClock fills the whole workspace (24×48) — it's an embedded dashboard.
  hamclock: { w: 24, h: 48 },
};

const FALLBACK_SPAN = { w: 8, h: 8 };
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

/** Compute a placement for a brand-new tile. The fixed-lattice workspace does
 *  NOT compact at render time, so this finds a home for the tile itself:
 *  first-fit, scanning top-to-bottom then left-to-right for the first slot the
 *  tile's footprint fits without overlapping an existing tile. That fills any
 *  free space the operator has opened up (e.g. after removing a panel) instead
 *  of always appending at the bottom — which would extend the layout downward
 *  and shrink every other panel to keep the workspace inside the viewport.
 *  Only when no interior gap fits does it append below everything. */
export function placeTileInGrid(
  panelId: string,
  others: WorkspaceTile[],
): { x: number; y: number; w: number; h: number } {
  const span = defaultSpanFor(panelId);
  const w = Math.min(span.w, WORKSPACE_GRID_COLS);
  const h = span.h;
  const maxX = WORKSPACE_GRID_COLS - w;
  const bottom = others.reduce((m, t) => Math.max(m, t.y + t.h), 0);

  for (let y = 0; y <= bottom; y += 1) {
    for (let x = 0; x <= maxX; x += 1) {
      const candidate = { x, y, w, h };
      if (!others.some((t) => tilesOverlap(candidate, t))) {
        return candidate;
      }
    }
  }
  // Nothing fits inside the current extent — append below everything (free).
  return { x: 0, y: bottom, w, h };
}

function tilesOverlap(
  a: { x: number; y: number; w: number; h: number },
  b: { x: number; y: number; w: number; h: number },
): boolean {
  return (
    a.x < b.x + b.w &&
    a.x + a.w > b.x &&
    a.y < b.y + b.h &&
    a.y + a.h > b.y
  );
}

/** Best-effort parser + validator for the opaque /api/ui/layout JSON blob.
 *  Anything malformed falls through to the empty layout — never throws. The
 *  caller decides whether the empty value means "blank workspace" or
 *  "substitute the default workspace." Unknown panelIds are kept rather than dropped —
 *  plugin panels register asynchronously at startup, and dropping their
 *  tiles on each layout deserialise would mean operators have to re-add
 *  plugin panels after every tab switch / page reload. The tile renderer
 *  treats an unresolved panelId as "render nothing until it shows up"
 *  (PanelTile / PanelBody both early-return null on missing def), so a
 *  tile pointing at a permanently-removed panel id is harmless. */
export function parseWorkspaceLayout(raw: unknown): WorkspaceLayout {
  if (!raw || typeof raw !== 'object') return EMPTY_WORKSPACE_LAYOUT;
  // Read schemaVersion as a plain number (not the literal-8 type that
  // Partial<WorkspaceLayout> would impose) so the v7/v8 comparison typechecks.
  const obj = raw as { schemaVersion?: unknown; tiles?: unknown; locked?: unknown };
  const version =
    typeof obj.schemaVersion === 'number' ? obj.schemaVersion : undefined;
  // v8 is the current 24×48 grid; v7 is the legacy 12×24 grid, migrated
  // forward by scaling every coordinate by GRID_SCALE so old saved layouts
  // render identically on the finer grid. Anything else (older formats,
  // future versions) falls through to the empty layout; callers decide
  // whether to render that as blank or substitute DEFAULT_WORKSPACE_LAYOUT.
  if (version !== 7 && version !== 8) return EMPTY_WORKSPACE_LAYOUT;
  const scale = version === 7 ? GRID_SCALE : 1;
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
      x: (tile.x as number) * scale,
      y: (tile.y as number) * scale,
      w: (tile.w as number) * scale,
      h: (tile.h as number) * scale,
      ...(tile.instanceConfig !== undefined
        ? { instanceConfig: tile.instanceConfig }
        : {}),
      ...(tile.locked === true ? { locked: true } : {}),
      // Pixel value — preserved verbatim, never scaled by GRID_SCALE.
      ...(typeof tile.lockedHeightPx === 'number' &&
      Number.isFinite(tile.lockedHeightPx) &&
      tile.lockedHeightPx > 0
        ? { lockedHeightPx: tile.lockedHeightPx }
        : {}),
    });
  }
  return {
    schemaVersion: 8,
    tiles: normalizeOversizedRows(tiles),
    ...(obj.locked === true ? { locked: true } : {}),
  };
}

function normalizeOversizedRows(tiles: WorkspaceTile[]): WorkspaceTile[] {
  const rows = tiles.reduce((max, t) => Math.max(max, t.y + t.h), 0);
  if (rows <= WORKSPACE_OVERSIZED_LAYOUT_ROW_THRESHOLD) return tiles;

  const scale = WORKSPACE_OVERSIZED_LAYOUT_NORMALIZED_ROWS / rows;
  return tiles.map((tile) => ({
    ...tile,
    y: Math.max(0, Math.round(tile.y * scale)),
    h: Math.max(1, Math.round(tile.h * scale)),
  }));
}
