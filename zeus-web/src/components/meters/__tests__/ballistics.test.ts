// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the shared meter ballistics primitives.

import { describe, expect, it } from 'vitest';
import {
  METER_BALLISTICS_DEFAULTS,
  ballisticsStep,
  isSilentSample,
  makeAverager,
  peakHoldStep,
} from '../ballistics';

describe('isSilentSample', () => {
  it('treats -200 dBFS and below as silent', () => {
    expect(isSilentSample(-200)).toBe(true);
    expect(isSilentSample(-300)).toBe(true);
    expect(isSilentSample(-199.99)).toBe(false);
  });
  it('treats non-finite as silent', () => {
    expect(isSilentSample(NaN)).toBe(true);
    expect(isSilentSample(-Infinity)).toBe(true);
    expect(isSilentSample(Infinity)).toBe(true);
  });
  it('treats normal readings as live', () => {
    expect(isSilentSample(-73)).toBe(false);
    expect(isSilentSample(0)).toBe(false);
    expect(isSilentSample(100)).toBe(false);
  });
});

describe('ballisticsStep', () => {
  it('uses attack tau when rising', () => {
    // 1 attack tau (50 ms) should cover ~63 % of the gap.
    const next = ballisticsStep(0, 1, 0.05, 0.05, 0.6);
    expect(next).toBeGreaterThan(0.6);
    expect(next).toBeLessThan(0.66);
  });
  it('uses decay tau when falling', () => {
    // 0.6 sec is exactly one decay tau → ~63 % of gap covered.
    const next = ballisticsStep(1, 0, 0.6, 0.05, 0.6);
    expect(next).toBeGreaterThan(0.34);
    expect(next).toBeLessThan(0.40);
  });
  it('snaps to target when tau is near zero', () => {
    expect(ballisticsStep(0, 1, 0.01, 0, 0)).toBe(1);
  });
  it('stays put when prev == target', () => {
    expect(ballisticsStep(0.5, 0.5, 0.1, 0.05, 0.6)).toBe(0.5);
  });
});

describe('peakHoldStep', () => {
  it('rises instantly when value exceeds prev peak', () => {
    expect(peakHoldStep(0.3, 0.8, 0.5, 0, 1)).toBe(0.8);
  });
  it('decays at the configured fraction-per-second of axis span', () => {
    // peak 1.0, value 0.0, dt 1s, span 1, decay 0.05/sec → 0.95
    const next = peakHoldStep(1, 0, 1, 0, 1);
    expect(next).toBeCloseTo(0.95, 5);
  });
  it('decay rate scales with axis span', () => {
    // span 100 W, 1 sec, decay 0.05/sec → drops by 5 W
    const next = peakHoldStep(50, 10, 1, 0, 100);
    expect(next).toBeCloseTo(45, 5);
  });
  it('never falls below current value', () => {
    // value 0.6 floors the decay.
    const next = peakHoldStep(0.7, 0.6, 100, 0, 1);
    expect(next).toBe(0.6);
  });
});

describe('makeAverager', () => {
  it('returns the running mean', () => {
    const a = makeAverager(4);
    expect(a.push(10)).toBe(10);
    expect(a.push(20)).toBe(15);
    expect(a.push(30)).toBe(20);
    expect(a.push(40)).toBe(25);
    // Buffer full — next push drops the 10.
    expect(a.push(50)).toBe(35);
  });
  it('resize seeds the new buffer with the current mean', () => {
    const a = makeAverager(2);
    a.push(10);
    a.push(20); // mean = 15
    a.resize(4);
    // After resize the new buffer is full of 15s; pushing 35 should yield
    // (15 + 15 + 15 + 35) / 4 = 20.
    expect(a.push(35)).toBe(20);
  });
  it('reset clears all history so the next push returns itself', () => {
    const a = makeAverager(4);
    a.push(100);
    a.push(100);
    a.reset();
    expect(a.push(5)).toBe(5);
  });
  it('handles n=0 gracefully (clamps to size 1)', () => {
    const a = makeAverager(0);
    expect(a.push(7)).toBe(7);
    expect(a.push(9)).toBe(9);
  });
});

describe('METER_BALLISTICS_DEFAULTS', () => {
  it('pins the universal meter ballistic defaults', () => {
    // Analog S-meter dial keeps its own faster-attack defaults in
    // analogMeterStore.ts (operator-tunable, persisted). These constants
    // govern useBallisticReading — the shared hook every other meter
    // (TX-stage rows, meter-group widgets, mic bar) reads through. The
    // attack here is intentionally slower than the dial: a wide dial
    // reads a 50 ms attack as expressive; a small bar reads it as jitter.
    expect(METER_BALLISTICS_DEFAULTS.attackSec).toBe(0.35);
    expect(METER_BALLISTICS_DEFAULTS.decaySec).toBe(0.6);
    expect(METER_BALLISTICS_DEFAULTS.avgSamples).toBe(18);
    expect(METER_BALLISTICS_DEFAULTS.peakDecayFracPerSec).toBe(0.05);
    expect(METER_BALLISTICS_DEFAULTS.silentSentinel).toBe(-200);
  });
});
