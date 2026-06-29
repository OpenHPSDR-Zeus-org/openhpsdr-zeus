// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License v2 or later. See the
// LICENSE file at the repository root, or https://www.gnu.org/licenses/.

import { describe, expect, it } from 'vitest';
import { computeAutopanCenterHz } from './filter-autopan';

// 192 kHz span, USB 150..2850 filter, ~6% margin (11.52 kHz).
const SPAN = 192_000;
const MARGIN = SPAN * 0.06;
const base = {
  spanHz: SPAN,
  filterLowHz: 150,
  filterHighHz: 2850,
  mode: 'USB',
  cwPitchHz: 600,
  marginHz: MARGIN,
};

describe('computeAutopanCenterHz — keep filter/dial in view', () => {
  it('returns null when the dial sits on the view centre', () => {
    expect(
      computeAutopanCenterHz({ ...base, viewCenterHz: 14_200_000, vfoHz: 14_200_000 }),
    ).toBeNull();
  });

  it('returns null while the filter is comfortably inside the margin', () => {
    // Dial 50 kHz right of centre: filter high edge at ~+52.85 kHz, well within
    // the usable half-window (96 - 11.52 = 84.48 kHz).
    expect(
      computeAutopanCenterHz({ ...base, viewCenterHz: 14_200_000, vfoHz: 14_250_000 }),
    ).toBeNull();
  });

  it('pans right just enough to seat the filter high edge on the margin', () => {
    // Dial 90 kHz right: filter high edge at 14_290_000 + 2850 = 14_292_850.
    const viewCenterHz = 14_200_000;
    const vfoHz = 14_290_000;
    const next = computeAutopanCenterHz({ ...base, viewCenterHz, vfoHz });
    expect(next).not.toBeNull();
    // Edge-follow: high edge should now rest on the right margin line.
    const viewHi = next! + SPAN / 2 - MARGIN;
    expect(viewHi).toBeCloseTo(vfoHz + 2850, 3);
    expect(next!).toBeGreaterThan(viewCenterHz); // panned toward the dial
  });

  it('pans left when the dial walks off the low edge (carrier kept in view)', () => {
    // Dial 90 kHz left: carrier (vfo) is the leftmost extent for USB.
    const viewCenterHz = 14_200_000;
    const vfoHz = 14_110_000;
    const next = computeAutopanCenterHz({ ...base, viewCenterHz, vfoHz });
    expect(next).not.toBeNull();
    const viewLo = next! - SPAN / 2 + MARGIN;
    expect(viewLo).toBeCloseTo(vfoHz, 3); // carrier seated on the left margin
    expect(next!).toBeLessThan(viewCenterHz);
  });

  it('anchors the passband on the LO in CW (pitch-shifted), not the VFO', () => {
    // CWU: LO = vfo - pitch. With a symmetric narrow filter the band hangs off
    // the LO; verify the recentre accounts for the pitch shift.
    const cw = { ...base, mode: 'CWU', filterLowHz: -250, filterHighHz: 250 };
    const viewCenterHz = 14_200_000;
    const vfoHz = 14_295_000; // far right
    const next = computeAutopanCenterHz({ ...cw, viewCenterHz, vfoHz });
    expect(next).not.toBeNull();
    // Rightmost extent is max(vfo, lo+250) = vfo (since lo = vfo-600).
    const viewHi = next! + SPAN / 2 - MARGIN;
    expect(viewHi).toBeCloseTo(vfoHz, 3);
  });

  it('ignores zero / invalid geometry', () => {
    expect(computeAutopanCenterHz({ ...base, spanHz: 0, viewCenterHz: 1, vfoHz: 1 })).toBeNull();
    expect(
      computeAutopanCenterHz({ ...base, viewCenterHz: NaN, vfoHz: 14_200_000 }),
    ).toBeNull();
  });
});
