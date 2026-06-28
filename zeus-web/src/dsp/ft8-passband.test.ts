// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import {
  absHzForClientX,
  clampOffsetHz,
  isOffsetVisible,
  offsetHzForClientX,
  pixelForOffsetHz,
  resolveWaterfallClick,
  snapOffsetToDecode,
  spanHzOf,
  type PassbandView,
} from './ft8-passband';

// CTUN-centred framing geometry: the panel centres at dial + 1400 Hz, so the
// audio passband (0..3000) straddles the centre line. span = width*hzPerPixel.
const DIAL = 14_074_000;
const VIEW: PassbandView = { centerHz: DIAL + 1400, hzPerPixel: 2, width: 2000 }; // 4 kHz span

describe('ft8-passband geometry', () => {
  it('span is width * hzPerPixel, 0 when geometry absent', () => {
    expect(spanHzOf(VIEW)).toBe(4000);
    expect(spanHzOf({ centerHz: 0, hzPerPixel: 0, width: 0 })).toBe(0);
  });

  it('maps a click X to the absolute RF frequency at the slice centre', () => {
    // Centre of a 1000px element → the slice centre frequency.
    expect(absHzForClientX(500, 0, 1000, VIEW)).toBe(DIAL + 1400);
    // Left edge → centre − span/2; right edge → centre + span/2.
    expect(absHzForClientX(0, 0, 1000, VIEW)).toBe(DIAL + 1400 - 2000);
    expect(absHzForClientX(1000, 0, 1000, VIEW)).toBe(DIAL + 1400 + 2000);
  });

  it('converts a click X to a clamped audio offset', () => {
    // Centre → dial + 1400.
    expect(offsetHzForClientX(500, 0, 1000, VIEW, DIAL)).toBe(1400);
    // Far left maps below 0 → clamped to 0; far right above 3000 → clamped.
    expect(offsetHzForClientX(0, 0, 1000, VIEW, DIAL)).toBe(0);
    expect(offsetHzForClientX(1000, 0, 1000, VIEW, DIAL)).toBe(3000);
  });

  it('round-trips offset ↔ pixel', () => {
    for (const offset of [0, 800, 1400, 2000, 3000]) {
      const px = pixelForOffsetHz(offset, 1000, VIEW, DIAL);
      const back = offsetHzForClientX(px, 0, 1000, VIEW, DIAL);
      expect(back).toBeCloseTo(offset, 6);
    }
  });

  it('pixelForOffsetHz(_, 100, …) yields a left-percentage', () => {
    // Centre offset (1400) → 50%.
    expect(pixelForOffsetHz(1400, 100, VIEW, DIAL)).toBeCloseTo(50, 6);
  });

  it('reports off-screen offsets as not visible', () => {
    // dial+1400 visible; a far-out RF offset (beyond the 4 kHz window) is not.
    expect(isOffsetVisible(1400, VIEW, DIAL)).toBe(true);
    // Offset whose absolute Hz lands outside the window.
    const narrow: PassbandView = { centerHz: DIAL + 1400, hzPerPixel: 0.5, width: 200 }; // 100 Hz span
    expect(isOffsetVisible(0, narrow, DIAL)).toBe(false);
  });
});

describe('clampOffsetHz', () => {
  it('clamps to the FT8 passband', () => {
    expect(clampOffsetHz(-50)).toBe(0);
    expect(clampOffsetHz(3500)).toBe(3000);
    expect(clampOffsetHz(1500)).toBe(1500);
    expect(clampOffsetHz(Number.NaN)).toBe(0);
  });
});

describe('snapOffsetToDecode', () => {
  // span 4000 over 1000px → 4 Hz/px; 30px radius → 120 Hz.
  const decodes = [1500, 2400];
  it('snaps to a decode within the pixel radius', () => {
    expect(snapOffsetToDecode(1450, decodes, 1000, VIEW)).toBe(1500);
  });
  it('leaves the click alone past the radius', () => {
    expect(snapOffsetToDecode(1700, decodes, 1000, VIEW)).toBe(1700);
  });
  it('is a no-op with no decodes', () => {
    expect(snapOffsetToDecode(1700, [], 1000, VIEW)).toBe(1700);
  });
});

describe('resolveWaterfallClick — HOLD TX FREQ', () => {
  it('moves both RX and TX when not held', () => {
    expect(resolveWaterfallClick(1500, false)).toEqual({ rxFocusHz: 1500, txOffsetHz: 1500 });
  });
  it('moves only RX when HOLD TX FREQ is engaged', () => {
    expect(resolveWaterfallClick(1500, true)).toEqual({ rxFocusHz: 1500, txOffsetHz: null });
  });
});
