// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the animated view-center (issue #597 Phase 1). Uses the
// injectable clock/raf so the tween is driven deterministically — no
// browser, no real time.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import * as vc from './view-center';

// Manual clock + rAF harness.
let nowMs = 0;
let scheduled: ((t: number) => void) | null = null;
let nextHandle = 1;

function stepFrame(dtMs: number): void {
  nowMs += dtMs;
  const cb = scheduled;
  scheduled = null;
  cb?.(nowMs);
}

function drainFrames(dtMs: number, maxFrames = 1000): number {
  let frames = 0;
  while (scheduled && frames < maxFrames) {
    stepFrame(dtMs);
    frames++;
  }
  return frames;
}

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

describe('view-center tween', () => {
  it('starts parked with zero cost (no rAF scheduled)', () => {
    expect(vc._isLoopRunningForTest()).toBe(false);
    expect(scheduled).toBeNull();
  });

  it('initialises from the first frame without a glide', () => {
    vc.reconcileFrame(14_200_000, 93.75);
    expect(vc.getViewCenterHz()).toBe(14_200_000);
    expect(vc.getTargetCenterHz()).toBe(14_200_000);
    expect(vc.isInitialized()).toBe(true);
    expect(scheduled).toBeNull(); // snap, not tween
  });

  it('converges to the target and self-parks', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(500);
    expect(scheduled).not.toBeNull(); // loop armed
    const frames = drainFrames(16.7);
    expect(frames).toBeLessThan(60); // converges well under a second
    expect(vc.getViewCenterHz()).toBe(14_200_500);
    expect(scheduled).toBeNull(); // parked again — zero idle cost
  });

  it('moves monotonically toward the target (no overshoot, no reversal)', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(500);
    let prev = vc.getViewCenterHz();
    while (scheduled) {
      stepFrame(16.7);
      const cur = vc.getViewCenterHz();
      expect(cur).toBeGreaterThanOrEqual(prev); // the reversal-counter invariant
      expect(cur).toBeLessThanOrEqual(14_200_500);
      prev = cur;
    }
  });

  it('accumulates bursty wheel notches into one continuous glide', () => {
    vc.snapTo(14_200_000, 93.75);
    // 10 notches over ~500 ms, mid-glide
    for (let i = 0; i < 10; i++) {
      vc.nudgeTargetHz(500);
      stepFrame(50);
    }
    expect(vc.getTargetCenterHz()).toBe(14_205_000);
    drainFrames(16.7);
    expect(vc.getViewCenterHz()).toBe(14_205_000);
  });

  it('clamps dt across a GC pause instead of teleporting', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(10_000);
    // A 500 ms stall — the integrator must treat it as MAX_DT (50 ms), so
    // the view moves at most 1 - e^(-50/70) ≈ 51% of the gap, not 99.9%.
    stepFrame(500);
    const moved = vc.getViewCenterHz() - 14_200_000;
    expect(moved).toBeLessThan(10_000 * 0.55);
    expect(moved).toBeGreaterThan(0);
  });

  it('notifies subscribers per tick and stops when parked', () => {
    vc.snapTo(14_200_000, 93.75);
    let calls = 0;
    const unsub = vc.subscribe(() => calls++);
    vc.nudgeTargetHz(500);
    drainFrames(16.7);
    const callsAtPark = calls;
    expect(callsAtPark).toBeGreaterThan(0);
    nowMs += 1000; // idle time — no frames, no notifications
    expect(calls).toBe(callsAtPark);
    unsub();
  });
});

describe('reconcileFrame vs optimistic tuning', () => {
  it('ignores lagging frame centers during an active gesture', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(500); // operator just tuned
    // A frame stamped at the OLD center arrives 100 ms later — must not
    // drag the target backward.
    nowMs += 100;
    const external = vc.reconcileFrame(14_200_000, 93.75);
    expect(external).toBe(false);
    expect(vc.getTargetCenterHz()).toBe(14_200_500);
  });

  it('recognises an external tune when the dial has been quiet', () => {
    vc.snapTo(14_200_000, 93.75);
    nowMs += 5_000; // long quiet
    const external = vc.reconcileFrame(14_350_000, 93.75);
    expect(external).toBe(true);
    expect(vc.getTargetCenterHz()).toBe(14_350_000);
    expect(scheduled).not.toBeNull(); // glides there
    // ...and the external retune armed the refill hold.
    expect(vc.isWithinRefillHold(500)).toBe(true);
  });

  it('treats settled frames as agreement (no retarget churn)', () => {
    vc.snapTo(14_200_000, 93.75);
    nowMs += 5_000;
    const external = vc.reconcileFrame(14_200_000, 93.75);
    expect(external).toBe(false);
    expect(scheduled).toBeNull();
  });
});

describe('refill hold', () => {
  it('opens on a tune and expires after holdMs', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(500);
    expect(vc.isWithinRefillHold(240)).toBe(true);
    nowMs += 239;
    expect(vc.isWithinRefillHold(240)).toBe(true);
    nowMs += 2;
    expect(vc.isWithinRefillHold(240)).toBe(false);
  });

  it('restarts on every target change (sustained drag keeps it open)', () => {
    vc.snapTo(14_200_000, 93.75);
    for (let i = 0; i < 5; i++) {
      vc.nudgeTargetHz(500);
      nowMs += 100;
      expect(vc.isWithinRefillHold(240)).toBe(true);
    }
  });

  it('is cleared by snapTo (reset path — zoom / sample-rate change)', () => {
    vc.snapTo(14_200_000, 93.75);
    vc.nudgeTargetHz(500);
    expect(vc.isWithinRefillHold(240)).toBe(true);
    vc.snapTo(14_200_500, 93.75);
    expect(vc.isWithinRefillHold(240)).toBe(false);
  });

  it('derives holdMs from the sample rate, clamped to the radio range', () => {
    // 16384-pt FFT: 192 kHz ≈ 85 ms fill → ~240 ms hold; 48 kHz ≈ 341 ms
    // fill → ~508 ms hold.
    const hold192 = vc.refillHoldMsForSampleRate(192_000);
    const hold48 = vc.refillHoldMsForSampleRate(48_000);
    expect(hold192).toBeGreaterThan(200);
    expect(hold192).toBeLessThan(300);
    expect(hold48).toBeGreaterThan(450);
    expect(hold48).toBeLessThan(560);
    // Bogus rates clamp instead of producing multi-second holds.
    expect(vc.refillHoldMsForSampleRate(0)).toBe(vc.refillHoldMsForSampleRate(192_000));
    expect(vc.refillHoldMsForSampleRate(1)).toBe(vc.refillHoldMsForSampleRate(48_000));
  });
});

describe('delta-form semantics (CW safety)', () => {
  it('a wheel-then-drag interleave never feeds an absolute into the center', () => {
    // Frame center is dial − 700 (CWU pitch). The view tracks frame space;
    // deltas keep it there.
    const pitch = 700;
    const dial = 7_030_000;
    vc.snapTo(dial - pitch, 93.75);
    // wheel +500 (dial space delta == center space delta)
    vc.nudgeTargetHz(500);
    // drag commit: snapped dial-space value minus commanded dial-space value
    const commanded = dial + 500;
    const snapped = dial + 1_000;
    vc.nudgeTargetHz(snapped - commanded);
    drainFrames(16.7);
    // view ends at (dial + 1000) − pitch: pitch never leaked in.
    expect(vc.getViewCenterHz()).toBe(dial + 1_000 - pitch);
  });

  it('zero-delta nudges only stamp the optimistic clock', () => {
    vc.snapTo(14_200_000, 93.75);
    nowMs += 5_000;
    vc.nudgeTargetHz(0);
    expect(vc.getTargetCenterHz()).toBe(14_200_000);
    expect(scheduled).toBeNull();
    expect(vc.msSinceOptimisticTune()).toBe(0);
  });
});
