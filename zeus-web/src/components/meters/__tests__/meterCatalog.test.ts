// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';
import {
  METER_CATALOG,
  METER_FILTERS,
  METER_READINGS,
  MeterReadingId,
  meterMatchesFilter,
  zoneTransitionTicks,
  type MeterReadingDef,
} from '../meterCatalog';

describe('meterCatalog', () => {
  it('has a catalog entry for every MeterReadingId', () => {
    const ids = Object.values(MeterReadingId);
    for (const id of ids) {
      const def = METER_CATALOG[id];
      expect(def, `missing entry for ${id}`).toBeDefined();
      expect(def.id).toBe(id);
      expect(def.label.length).toBeGreaterThan(0);
      expect(def.short.length).toBeGreaterThan(0);
    }
  });

  it('has 27 readings (matches plan §3.1)', () => {
    expect(METER_READINGS.length).toBe(27);
  });

  it('every entry uses a known color token', () => {
    const allowed = new Set(['amber-signal', 'power', 'tx', 'accent']);
    for (const def of METER_READINGS) {
      expect(allowed.has(def.colorToken), `${def.id} has bad colorToken`).toBe(
        true,
      );
    }
  });

  it('every entry has sane axis defaults (min < max)', () => {
    for (const def of METER_READINGS) {
      expect(def.defaultMin).toBeLessThan(def.defaultMax);
    }
  });

  it('rx-signal entries default to amber-signal token', () => {
    for (const def of METER_READINGS) {
      if (def.category === 'rx-signal') {
        expect(def.colorToken).toBe('amber-signal');
      }
    }
  });

  it('TxFwdWatts is power-yellow by default', () => {
    expect(METER_CATALOG[MeterReadingId.TxFwdWatts].colorToken).toBe('power');
  });

  it('TxSwr defaults to a bigarc widget kind (immersive SWR gauge)', () => {
    expect(METER_CATALOG[MeterReadingId.TxSwr].defaultKind).toBe('bigarc');
  });

  it('TxFwdWatts defaults to a bigarc widget kind (immersive watts gauge)', () => {
    expect(METER_CATALOG[MeterReadingId.TxFwdWatts].defaultKind).toBe('bigarc');
  });

  it('library filter "rx" includes signal/adc/agc and excludes tx', () => {
    for (const def of METER_READINGS) {
      const isRx =
        def.category === 'rx-signal' ||
        def.category === 'rx-adc' ||
        def.category === 'rx-agc';
      expect(meterMatchesFilter(def, 'rx')).toBe(isRx);
    }
  });

  it('library filter "tx" excludes RX entries', () => {
    for (const def of METER_READINGS) {
      const isTx = def.category.startsWith('tx-');
      expect(meterMatchesFilter(def, 'tx')).toBe(isTx);
    }
  });

  it('library filter "all" includes everything', () => {
    for (const def of METER_READINGS) {
      expect(meterMatchesFilter(def, 'all')).toBe(true);
    }
  });

  it('METER_FILTERS contains the six expected chips in order', () => {
    expect([...METER_FILTERS]).toEqual([
      'all',
      'rx',
      'tx',
      'power',
      'stage',
      'agc',
    ]);
  });

  it('warn/danger thresholds are ordered (warn < danger) when both present', () => {
    for (const def of METER_READINGS) {
      if (def.warnAt !== undefined && def.dangerAt !== undefined) {
        expect(
          def.warnAt < def.dangerAt,
          `${def.id} warn>=danger`,
        ).toBe(true);
      }
    }
  });
});

describe('zoneTransitionTicks', () => {
  it('TxMicPk emits four ticks at -25 / -20 / -10 / -3 dBFS', () => {
    const def = METER_CATALOG[MeterReadingId.TxMicPk];
    const ticks = zoneTransitionTicks(def, def.defaultMin, def.defaultMax);
    expect(ticks.length).toBe(4);
    // Span: max - min = 12 - (-30) = 42; boundaries at -25 / -20 / -10 / -3.
    const span = def.defaultMax - def.defaultMin;
    const expected: ReadonlyArray<{ at: number; level: 'ok' | 'warn' | 'danger' }> = [
      { at: -25, level: 'warn' },
      { at: -20, level: 'ok' },
      { at: -10, level: 'warn' },
      { at: -3, level: 'danger' },
    ];
    for (let i = 0; i < expected.length; i++) {
      const want = expected[i]!;
      const tick = ticks[i]!;
      expect(tick.level).toBe(want.level);
      expect(tick.frac).toBeCloseTo((want.at - def.defaultMin) / span, 5);
    }
  });

  it('TxFwdWatts (3-zone derivation) emits two ticks at warnAt and dangerAt', () => {
    const def = METER_CATALOG[MeterReadingId.TxFwdWatts];
    const ticks = zoneTransitionTicks(def, def.defaultMin, def.defaultMax);
    expect(ticks.length).toBe(2);
    const t0 = ticks[0]!;
    const t1 = ticks[1]!;
    expect(t0.level).toBe('warn');
    expect(t0.frac).toBeCloseTo(0.9, 5); // 4.5 / 5
    expect(t1.level).toBe('danger');
    // dangerAt === defaultMax → frac=1.0 must be retained (rated-rail tick).
    expect(t1.frac).toBeCloseTo(1.0, 5);
  });

  it('skips ticks with frac < 0.02 (gauge-anchor edge rule)', () => {
    // Synthetic def: explicit zones with a level boundary at 0.5/100 = 0.005.
    const def: MeterReadingDef = {
      id: MeterReadingId.TxFwdWatts,
      label: 'synthetic',
      short: 'syn',
      category: 'tx-power',
      unit: 'W',
      defaultMin: 0,
      defaultMax: 100,
      colorToken: 'power',
      defaultKind: 'hbar',
      zones: [
        { from: 0, to: 0.5, level: 'ok' },
        { from: 0.5, to: 100, level: 'danger' },
      ],
    };
    const ticks = zoneTransitionTicks(def, 0, 100);
    // Boundary at 0.5 → frac = 0.005 → below the 0.02 threshold → skipped.
    expect(ticks.length).toBe(0);
  });

  it('keeps a tick at exactly frac = 1.0 (rated-rail allowed)', () => {
    // Synthetic def: boundary at the axis maximum.
    const def: MeterReadingDef = {
      id: MeterReadingId.TxFwdWatts,
      label: 'synthetic',
      short: 'syn',
      category: 'tx-power',
      unit: 'W',
      defaultMin: 0,
      defaultMax: 10,
      colorToken: 'power',
      defaultKind: 'hbar',
      zones: [
        { from: 0, to: 10, level: 'ok' },
        { from: 10, to: 10, level: 'danger' },
      ],
    };
    const ticks = zoneTransitionTicks(def, 0, 10);
    expect(ticks.length).toBe(1);
    const t = ticks[0]!;
    expect(t.frac).toBeCloseTo(1.0, 5);
    expect(t.level).toBe('danger');
  });

  it('returns empty when the reading has no zones (no thresholds)', () => {
    // RxAgcGain has neither warnAt/dangerAt nor explicit zones.
    const def = METER_CATALOG[MeterReadingId.RxAgcGain];
    const ticks = zoneTransitionTicks(def, def.defaultMin, def.defaultMax);
    expect(ticks).toEqual([]);
  });

  it('collapses adjacent zones with the same level (no false ticks)', () => {
    // Two consecutive 'ok' zones — no colour change → no tick at the seam.
    const def: MeterReadingDef = {
      id: MeterReadingId.TxFwdWatts,
      label: 'synthetic',
      short: 'syn',
      category: 'tx-power',
      unit: 'W',
      defaultMin: 0,
      defaultMax: 10,
      colorToken: 'power',
      defaultKind: 'hbar',
      zones: [
        { from: 0, to: 4, level: 'ok' },
        { from: 4, to: 7, level: 'ok' },
        { from: 7, to: 10, level: 'danger' },
      ],
    };
    const ticks = zoneTransitionTicks(def, 0, 10);
    expect(ticks.length).toBe(1);
    const t = ticks[0]!;
    expect(t.frac).toBeCloseTo(0.7, 5);
    expect(t.level).toBe('danger');
  });

  it('returns empty when min >= max (defensive)', () => {
    const def = METER_CATALOG[MeterReadingId.TxMicPk];
    expect(zoneTransitionTicks(def, 0, 0)).toEqual([]);
    expect(zoneTransitionTicks(def, 5, 5)).toEqual([]);
    expect(zoneTransitionTicks(def, 10, 5)).toEqual([]);
  });
});
