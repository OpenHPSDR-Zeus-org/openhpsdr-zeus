// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// CTUN (click-tune / centred-tuning) coupling invariants on the FRONTEND view
// model. The backend (RadioServiceCtunTests) pins the frozen-NCO / TX-on-dial
// semantics; here we pin the OTHER half the audit flagged as fragile: the
// pan-tune gesture's interaction with the #597 view-center.
//
// Under CTUN the dial roams but the frame center must NOT move, and an incoming
// server frame at the unchanged center must NOT be mistaken for an external
// retune (which would snap the view and desync the off-centre dial marker).
// The gesture achieves this by calling viewCenter.markOptimisticTune() WITHOUT
// viewCenter.nudgeTargetHz() — that is the contract this file locks down.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import * as vc from '../state/view-center';

// Manual clock + rAF harness (mirrors view-center.test.ts) so the optimistic
// window is driven deterministically — no browser, no real time.
let nowMs = 0;
let scheduled: ((t: number) => void) | null = null;
let nextHandle = 1;

beforeEach(() => {
  nowMs = 0;
  scheduled = null;
  vc._resetForTest();
  vc._setClockForTest(
    () => nowMs,
    (cb) => {
      scheduled = cb;
      return nextHandle++;
    },
    () => {
      scheduled = null;
    },
  );
});

afterEach(() => {
  vc._resetForTest();
});

describe('CTUN dial-move view-center coupling', () => {
  it('CTUN dial move leaves the view target fixed (no nudge)', () => {
    vc.snapTo(14_200_000, 93.75);
    expect(vc.getTargetCenterHz()).toBe(14_200_000);

    // CTUN gesture: stamp the optimistic clock but DO NOT nudge the target —
    // this is exactly what use-pan-tune-gesture.ts does on the ctunEnabled
    // branch of commitFinal / nudgeVfo / drag.
    vc.markOptimisticTune();

    // The view stays put; the dial roams independently (rendered off-centre as
    // vfo − targetCenter by PassbandOverlay / Waterfall).
    expect(vc.getTargetCenterHz()).toBe(14_200_000);
    expect(vc.getViewCenterHz()).toBe(14_200_000);
    // No tween armed — the display does not glide on a CTUN dial move.
    expect(scheduled).toBeNull();
  });

  it('a frame at the unchanged center after a CTUN move is NOT an external retune', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.markOptimisticTune(); // CTUN dial move — frame center unchanged

    // Server frame still reports the frozen NCO center (CTUN never moved it).
    // reconcileFrame must report "not external" so no snapTo / refill-hold fires.
    const external = vc.reconcileFrame(14_200_000, 93.75);
    expect(external).toBe(false);
    expect(vc.getTargetCenterHz()).toBe(14_200_000);
    expect(scheduled).toBeNull();
  });

  it('the optimistic window suppresses a spurious snap even if a frame center drifts within it', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.markOptimisticTune();

    // Within the optimistic window, even a mismatched center is treated as the
    // operator's gesture lagging — not an external retune. This is the coupling
    // the audit called out (CTUN correctness leans on this window).
    nowMs += 100; // < OPTIMISTIC_WINDOW_MS (400)
    const external = vc.reconcileFrame(14_205_000, 93.75);
    expect(external).toBe(false);
  });

  it('CONTRAST: a non-CTUN gesture DOES nudge the target (sanity that the test discriminates)', () => {
    vc.snapTo(14_200_000, 93.75);

    // Classic tune (CTUN off) nudges the view target — this is the branch the
    // CTUN gesture deliberately skips. If markOptimisticTune ever started
    // moving the target, the first test would still pass but this one pins the
    // difference so the two code paths can't silently converge.
    vc.nudgeTargetHz(500);
    expect(vc.getTargetCenterHz()).toBe(14_200_500);
    expect(scheduled).not.toBeNull(); // glide armed
  });
});
