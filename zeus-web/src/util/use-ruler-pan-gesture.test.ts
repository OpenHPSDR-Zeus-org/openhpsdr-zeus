// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { rulerDragTargetHz } from './use-ruler-pan-gesture';

describe('rulerDragTargetHz', () => {
  it('moves content with the grab direction', () => {
    expect(rulerDragTargetHz(28_000_000, 500, 600, 1000, 100_000)).toBe(27_990_000);
    expect(rulerDragTargetHz(28_000_000, 500, 400, 1000, 100_000)).toBe(28_010_000);
  });

  it('can pan by more than the visible span', () => {
    expect(rulerDragTargetHz(28_000_000, 500, 1700, 1000, 100_000)).toBe(27_880_000);
    expect(rulerDragTargetHz(28_000_000, 500, -700, 1000, 100_000)).toBe(28_120_000);
  });

  it('clamps to the supported radio range', () => {
    expect(rulerDragTargetHz(1000, 0, 100, 100, 10_000)).toBe(0);
    expect(rulerDragTargetHz(59_999_000, 100, 0, 100, 10_000)).toBe(60_000_000);
  });
});
