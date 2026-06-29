// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import {
  WORKSPACE_OVERSIZED_LAYOUT_NORMALIZED_ROWS,
  parseWorkspaceLayout,
  placeTileInGrid,
  placeTileInPage,
  type WorkspaceTile,
} from './workspace';

function tile(p: Partial<WorkspaceTile> & { uid: string }): WorkspaceTile {
  return { panelId: 'smeter', x: 0, y: 0, w: 6, h: 4, ...p };
}

function overlaps(
  a: { x: number; y: number; w: number; h: number },
  b: { x: number; y: number; w: number; h: number },
): boolean {
  return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

function maxRows(layout: ReturnType<typeof parseWorkspaceLayout>): number {
  return layout.tiles.reduce((max, t) => Math.max(max, t.y + t.h), 0);
}

describe('parseWorkspaceLayout', () => {
  it('normalizes pathologically oversized saved row counts', () => {
    const layout = parseWorkspaceLayout({
      schemaVersion: 8,
      tiles: [
        { uid: 'tile-hero', panelId: 'hero', x: 0, y: 0, w: 20, h: 872 },
        { uid: 'tile-vfo', panelId: 'vfo', x: 20, y: 0, w: 4, h: 193 },
        { uid: 'tile-smeter', panelId: 'smeter', x: 20, y: 195, w: 4, h: 74 },
        { uid: 'tile-tx', panelId: 'tx', x: 20, y: 442, w: 4, h: 123 },
        { uid: 'tile-txmeters', panelId: 'txmeters', x: 20, y: 786, w: 4, h: 97 },
      ],
    });

    expect(maxRows(layout)).toBeLessThanOrEqual(
      WORKSPACE_OVERSIZED_LAYOUT_NORMALIZED_ROWS,
    );
    expect(layout.tiles[0]).toMatchObject({
      uid: 'tile-hero',
      panelId: 'hero',
      x: 0,
      w: 20,
      y: 0,
      h: 119,
    });
    expect(layout.tiles[4]).toMatchObject({
      uid: 'tile-txmeters',
      panelId: 'txmeters',
      x: 20,
      w: 4,
      y: 107,
      h: 13,
    });
  });

  it('keeps legitimate dense layouts unchanged', () => {
    const layout = parseWorkspaceLayout({
      schemaVersion: 8,
      tiles: [
        { uid: 'tile-filter', panelId: 'filter', x: 0, y: 0, w: 20, h: 12 },
        { uid: 'tile-hero', panelId: 'hero', x: 0, y: 12, w: 20, h: 67 },
        { uid: 'tile-spots', panelId: 'spots', x: 0, y: 79, w: 7, h: 38 },
        { uid: 'tile-qrz', panelId: 'qrz', x: 13, y: 79, w: 7, h: 38 },
      ],
    });

    expect(maxRows(layout)).toBe(117);
    expect(layout.tiles).toEqual([
      { uid: 'tile-filter', panelId: 'filter', x: 0, y: 0, w: 20, h: 12 },
      { uid: 'tile-hero', panelId: 'hero', x: 0, y: 12, w: 20, h: 67 },
      { uid: 'tile-spots', panelId: 'spots', x: 0, y: 79, w: 7, h: 38 },
      { uid: 'tile-qrz', panelId: 'qrz', x: 13, y: 79, w: 7, h: 38 },
    ]);
  });

  it('preserves workspace and tile lock flags', () => {
    const layout = parseWorkspaceLayout({
      schemaVersion: 8,
      locked: true,
      tiles: [
        { uid: 'tile-hero', panelId: 'hero', x: 0, y: 0, w: 20, h: 24, locked: true },
        { uid: 'tile-vfo', panelId: 'vfo', x: 20, y: 0, w: 4, h: 8, locked: false },
      ],
    });

    expect(layout.locked).toBe(true);
    expect(layout.tiles[0]).toMatchObject({ uid: 'tile-hero', locked: true });
    expect(layout.tiles[1]).not.toHaveProperty('locked');
  });
});

describe('placeTileInGrid', () => {
  it('places the first tile at the origin', () => {
    expect(placeTileInGrid('smeter', [])).toMatchObject({ x: 0, y: 0 });
  });

  it('fills an interior gap instead of appending at the bottom', () => {
    // A wide tile fills x0..17; x18..23 is open. A new smeter (6 wide) must slot
    // into that free top-right space, NOT drop to the bottom and shrink
    // everything (the old code relied on a render-time compactor that is gone).
    const others = [tile({ uid: 'big', x: 0, y: 0, w: 18, h: 10 })];
    expect(placeTileInGrid('smeter', others)).toMatchObject({ x: 18, y: 0 });
  });

  it('appends below everything when no interior gap fits', () => {
    // A full-width band leaves no room until the next row down.
    const others = [tile({ uid: 'band', x: 0, y: 0, w: 24, h: 4 })];
    expect(placeTileInGrid('smeter', others)).toMatchObject({ x: 0, y: 4 });
  });

  it('never overlaps an existing tile', () => {
    const others = [
      tile({ uid: 'filter', x: 0, y: 0, w: 12, h: 10 }),
      tile({ uid: 'presets', x: 12, y: 0, w: 6, h: 10 }),
      tile({ uid: 'vfo', x: 18, y: 0, w: 6, h: 11 }),
      tile({ uid: 'hero', x: 0, y: 10, w: 18, h: 38 }),
    ];
    const placed = placeTileInGrid('qrz', others);
    expect(others.some((t) => overlaps(placed, t))).toBe(false);
  });
});

describe('placeTileInPage (pagination bounds)', () => {
  it('places within the page when a slot is free', () => {
    // cw is 8×8. Empty page → origin.
    expect(placeTileInPage('cw', [], 24, 48)).toEqual({ x: 0, y: 0, w: 8, h: 8 });
  });

  it('returns null when nothing fits the page (signals paginate)', () => {
    // A single tile filling the whole 24×48 page leaves no room for an 8×8.
    const full = [tile({ uid: 'full', x: 0, y: 0, w: 24, h: 48 })];
    expect(placeTileInPage('cw', full, 24, 48)).toBeNull();
  });

  it('finds a free slot inside the page rather than appending below', () => {
    // Top 24×8 band is taken; an 8×8 should drop to the next free row, NOT below
    // the page fold (placeTileInGrid would append below; this stays in-page).
    const others = [tile({ uid: 'band', x: 0, y: 0, w: 24, h: 8 })];
    const placed = placeTileInPage('cw', others, 24, 48);
    expect(placed).not.toBeNull();
    expect(placed!.y).toBe(8);
    expect(placed!.y + placed!.h).toBeLessThanOrEqual(48);
  });

  it('places an oversized tile at the origin instead of paginating forever', () => {
    // hero is 18×24; on a tiny 6×6 page it can never fit, so it must NOT return
    // null (which would spill endlessly) — it lands at the origin.
    expect(placeTileInPage('hero', [], 6, 6)).toMatchObject({ x: 0, y: 0 });
  });
});
