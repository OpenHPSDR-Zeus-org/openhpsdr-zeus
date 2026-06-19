// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import {
  WORKSPACE_OVERSIZED_LAYOUT_NORMALIZED_ROWS,
  parseWorkspaceLayout,
} from './workspace';

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
