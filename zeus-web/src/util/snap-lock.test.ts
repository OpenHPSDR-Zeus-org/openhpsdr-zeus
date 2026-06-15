// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License v2 or later. See the
// LICENSE file at the repository root, or https://www.gnu.org/licenses/.

import { describe, expect, it } from 'vitest';
import type { SnapLockMeasure } from '../dsp/signal-estimator';
import { snapLockStep, type SnapLockCfg, type SnapLockState } from './snap-lock';

const CFG: SnapLockCfg = { maxDriftHz: 500, deadbandHz: 40, maxStepHz: 80, releaseMissFrames: 25 };

function lockedAt(hz: number): SnapLockState {
  return { dialHz: hz, bodyHz: hz, originDialHz: hz, originBodyHz: hz, missFrames: 0 };
}

// snapLockStep ignores levelDb (the identity gate lives in measureSnapLock); a
// placeholder keeps these decision-core cases readable.
function meas(dialHz: number, bodyHz: number): SnapLockMeasure {
  return { dialHz, bodyHz, levelDb: -80 };
}

describe('snapLockStep — self-correcting decision core', () => {
  it('does nothing inside the deadband (no chatter)', () => {
    const r = snapLockStep(lockedAt(1000), meas(1020, 1020), CFG);
    expect(r.commitHz).toBeNull(); // 20 Hz < 40 Hz deadband
    expect(r.dialHz).toBe(1000);
    expect(r.release).toBe(false);
    expect(r.bodyHz).toBe(1020); // body still tracked silently
  });

  it('corrects past the deadband but clamps the per-frame step', () => {
    const r = snapLockStep(lockedAt(1000), meas(1300, 1300), CFG);
    // err 300 → clamped to +80; the dial creeps, never lurches.
    expect(r.dialHz).toBe(1080);
    expect(r.commitHz).toBe(1080);
  });

  it('clamps the target to ±maxDrift of the snap point (can never reach a far neighbour)', () => {
    // A measurement 1 kHz away (e.g. a momentary mis-measure toward a neighbour)
    // is clamped to origin+500 before the step is even taken.
    const r = snapLockStep(lockedAt(1000), meas(2000, 2000), CFG);
    expect(r.dialHz).toBe(1080); // still only +80 this frame, toward the 1500 cap
    expect(r.bodyHz).toBe(1500); // body anchor clamped to origin+maxDrift
  });

  it('counts a miss when the signal is gone, holding the dial', () => {
    const r = snapLockStep(lockedAt(1000), null, CFG);
    expect(r.missFrames).toBe(1);
    expect(r.commitHz).toBeNull();
    expect(r.dialHz).toBe(1000); // held, not moved
    expect(r.release).toBe(false);
  });

  it('releases after too many consecutive misses', () => {
    const r = snapLockStep({ ...lockedAt(1000), missFrames: 24 }, null, CFG);
    expect(r.missFrames).toBe(25);
    expect(r.release).toBe(true);
  });

  it('a fresh measurement resets the miss counter', () => {
    const r = snapLockStep({ ...lockedAt(1000), missFrames: 10 }, meas(1005, 1005), CFG);
    expect(r.missFrames).toBe(0);
  });
});
