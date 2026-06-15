// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the shared per-frame shift planner (issue #597 Phase 1).

import { beforeEach, describe, expect, it } from 'vitest';
import { planForFrame, plannedDataCenterHz, _resetFramePlanForTest } from './frame-plan';

const W = 2048;
const HZPP = 93.75;

beforeEach(() => {
  _resetFramePlanForTest();
});

describe('frame-plan memoization', () => {
  it('returns the identical decision object for repeat calls on one seq', () => {
    const a = planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    const b = planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    expect(b).toBe(a); // reference equality — both surfaces see one decision
  });

  it('advances the tracker exactly once per seq', () => {
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    // Second consumer of seq 1 must not re-plan against the updated tracker.
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    const d = planForFrame({ seq: 2, centerHz: 14_200_500n, hzPerPixel: HZPP, width: W });
    // 500 Hz / 93.75 ≈ 5.33 px → integer shift of 5
    expect(d).toMatchObject({ kind: 'shift', shiftPx: -5 });
  });
});

describe('frame-plan decision semantics (parity with the per-component trackers)', () => {
  it('first frame is a reset and seeds the tracker', () => {
    const d = planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    expect(d.kind).toBe('reset');
    expect(plannedDataCenterHz()).toBe(14_200_000n);
  });

  it('sub-pixel retunes accumulate across pushes (residual convention)', () => {
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    // +40 Hz: under half a pixel → push, tracker center unchanged
    const d2 = planForFrame({ seq: 2, centerHz: 14_200_040n, hzPerPixel: HZPP, width: W });
    expect(d2.kind).toBe('push');
    expect(plannedDataCenterHz()).toBe(14_200_000n);
    // another +40 Hz: now 80 Hz from the anchor ≈ 0.85 px → still push at
    // round() < 1? 80/93.75 = 0.853 → round = 1 → shift of -1
    const d3 = planForFrame({ seq: 3, centerHz: 14_200_080n, hzPerPixel: HZPP, width: W });
    expect(d3.kind).toBe('shift');
  });

  it('width changes reset but zoom changes rescale overlapping history', () => {
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    const dWidth = planForFrame({ seq: 2, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W / 2 });
    expect(dWidth.kind).toBe('reset');
    const dZoom = planForFrame({ seq: 3, centerHz: 14_200_000n, hzPerPixel: HZPP / 2, width: W / 2 });
    expect(dZoom).toMatchObject({ kind: 'rescale', srcXScale: 0.5 });
    expect(plannedDataCenterHz()).toBe(14_200_000n);
  });

  it('a move ≥ the full span is a reset', () => {
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    const spanHz = BigInt(Math.round(W * HZPP));
    const d = planForFrame({ seq: 2, centerHz: 14_200_000n + spanHz, hzPerPixel: HZPP, width: W });
    expect(d.kind).toBe('reset');
  });

  it('the tracker advances on every seq — invalid-payload frames included', () => {
    // The caller passes header fields regardless of pan/wf validity; two
    // consumers calling in any order across valid/invalid frames see the
    // same shift exactly once.
    planForFrame({ seq: 1, centerHz: 14_200_000n, hzPerPixel: HZPP, width: W });
    // "invalid" frame (no payload) still carries the retuned header:
    const d2 = planForFrame({ seq: 2, centerHz: 14_201_000n, hzPerPixel: HZPP, width: W });
    expect(d2.kind).toBe('shift');
    // next frame at the same center: no double shift
    const d3 = planForFrame({ seq: 3, centerHz: 14_201_000n, hzPerPixel: HZPP, width: W });
    expect(d3.kind).toBe('push');
  });
});
