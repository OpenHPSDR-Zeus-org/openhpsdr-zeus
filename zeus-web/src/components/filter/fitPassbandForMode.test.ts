// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';
import { fitPassbandForMode } from './miniPanMath';

const MARGIN = 120;

describe('fitPassbandForMode', () => {
  it('LSB: a signal above the carrier is the wrong sideband → no fit', () => {
    // Operator report 2026-06-14: clicking a signal above the VFO in LSB threw
    // the passband onto the USB (positive-offset) side.
    expect(fitPassbandForMode('LSB', 1250, 4430, MARGIN)).toBeNull();
  });

  it('LSB: a signal below the carrier stays on the negative sideband', () => {
    const r = fitPassbandForMode('LSB', -3000, -300, MARGIN);
    expect(r).not.toBeNull();
    expect(r!.low).toBeLessThan(0);
    expect(r!.high).toBeLessThanOrEqual(-50);
    expect(r!.high).toBeGreaterThan(r!.low);
  });

  it('LSB: a signal straddling the carrier is clamped below it', () => {
    const r = fitPassbandForMode('LSB', -2000, 800, MARGIN);
    expect(r).not.toBeNull();
    expect(r!.high).toBe(-50); // clamped to the carrier-side edge
    expect(r!.low).toBe(-2120);
  });

  it('USB: a signal below the carrier is the wrong sideband → no fit', () => {
    expect(fitPassbandForMode('USB', -4430, -1250, MARGIN)).toBeNull();
  });

  it('USB: a signal above the carrier stays on the positive sideband', () => {
    const r = fitPassbandForMode('USB', 300, 2700, MARGIN);
    expect(r).not.toBeNull();
    expect(r!.low).toBeGreaterThanOrEqual(50);
    expect(r!.high).toBeGreaterThan(r!.low);
  });

  it('CWL/CWU honour the sideband like SSB', () => {
    expect(fitPassbandForMode('CWL', 550, 650, MARGIN)).toBeNull();
    expect(fitPassbandForMode('CWU', -650, -550, MARGIN)).toBeNull();
    expect(fitPassbandForMode('CWL', -650, -550, MARGIN)).not.toBeNull();
  });

  it('symmetric modes pass the extent through unchanged (with margin)', () => {
    const r = fitPassbandForMode('AM', -2500, 2500, MARGIN);
    expect(r).toEqual({ low: -2620, high: 2620 });
  });

  it('DIG modes carry no sideband constraint', () => {
    expect(fitPassbandForMode('DIGU', -1000, 1000, MARGIN)).not.toBeNull();
    expect(fitPassbandForMode('DIGL', -1000, 1000, MARGIN)).not.toBeNull();
  });
});
