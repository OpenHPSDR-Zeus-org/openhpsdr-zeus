// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';
import { computeMeterGroupAutoFit } from './meterGroupConfig';

describe('computeMeterGroupAutoFit', () => {
  // Issue #1160: opening Settings tears down FlexWorkspace and remounts
  // it on close. The first effect run after a fresh mount must leave the
  // operator's saved size alone — otherwise the Meter Group tile snaps
  // back to its auto-fit size every time the gear panel is visited.
  it('returns null on the first mount even when current size differs from auto-fit', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: null,
      prevDirection: null,
      widgetCount: 2,
      direction: 'row',
      tileW: 8,
      tileH: 4,
    });
    expect(result).toBeNull();
  });

  it('returns null when widget count and direction are unchanged from the previous run', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 3,
      prevDirection: 'row',
      widgetCount: 3,
      direction: 'row',
      tileW: 8,
      tileH: 4,
    });
    expect(result).toBeNull();
  });

  it('snaps width to widget count when a widget is added in row direction', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 2,
      prevDirection: 'row',
      widgetCount: 4,
      direction: 'row',
      tileW: 2,
      tileH: 6,
    });
    expect(result).toEqual({ w: 4, h: 6 });
  });

  it('snaps height to widget count*3 (min 3) when a widget is added in column direction', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 1,
      prevDirection: 'column',
      widgetCount: 3,
      direction: 'column',
      tileW: 2,
      tileH: 3,
    });
    expect(result).toEqual({ w: 2, h: 9 });
  });

  it('snaps on direction flip from row to column', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 2,
      prevDirection: 'row',
      widgetCount: 2,
      direction: 'column',
      tileW: 2,
      tileH: 3,
    });
    expect(result).toEqual({ w: 2, h: 6 });
  });

  it('clamps widget count to at least 1 so an empty panel still has a minimum footprint', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 1,
      prevDirection: 'row',
      widgetCount: 0,
      direction: 'row',
      tileW: 4,
      tileH: 6,
    });
    expect(result).toEqual({ w: 1, h: 6 });
  });

  it('clamps column height to at least 3 grid rows', () => {
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 2,
      prevDirection: 'column',
      widgetCount: 0,
      direction: 'column',
      tileW: 2,
      tileH: 6,
    });
    expect(result).toEqual({ w: 2, h: 3 });
  });

  it('returns null when the computed auto-fit target equals current geometry', () => {
    // Widget count changed but target width matches what the tile
    // already has — no placement update needed.
    const result = computeMeterGroupAutoFit({
      prevWidgetCount: 2,
      prevDirection: 'row',
      widgetCount: 3,
      direction: 'row',
      tileW: 3,
      tileH: 6,
    });
    expect(result).toBeNull();
  });
});
