// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the pure spot-classification / filtering helpers in
// spots-store. These cover the band/mode mapping, QRT detection, and the
// settings-driven filter pipeline (band / mode / QRT / age / dedup) that the
// Spots panel applies to the server's cached snapshot.

import { describe, it, expect } from 'vitest';
import {
  spotModeToRxMode,
  spotModeGroup,
  freqHzToBand,
  spotIsQrt,
  applySpotSettingsFilters,
} from './spots-store';
import { SPOTS_SETTINGS_DEFAULTS, type ActivationSpotDto, type SpotsSettings } from '../api/client';

function spot(overrides: Partial<ActivationSpotDto>): ActivationSpotDto {
  return {
    source: 'POTA',
    activator: 'N0CALL',
    freqHz: 14_074_000,
    mode: 'SSB',
    reference: 'US-0001',
    name: null,
    location: null,
    grid: null,
    comments: null,
    spotter: null,
    spotTime: '2026-06-14T12:00:00',
    ...overrides,
  };
}

function settings(overrides: Partial<SpotsSettings>): SpotsSettings {
  return { ...SPOTS_SETTINGS_DEFAULTS, ...overrides };
}

describe('spotModeToRxMode', () => {
  it('picks LSB below 10 MHz and USB at/above for generic SSB', () => {
    expect(spotModeToRxMode('SSB', 7_200_000)).toBe('LSB');
    expect(spotModeToRxMode('SSB', 14_200_000)).toBe('USB');
    expect(spotModeToRxMode('SSB', 10_000_000)).toBe('USB');
  });

  it('honours explicit USB/LSB regardless of band', () => {
    expect(spotModeToRxMode('USB', 3_900_000)).toBe('USB');
    expect(spotModeToRxMode('LSB', 21_000_000)).toBe('LSB');
  });

  it('maps CW to the configured sideband', () => {
    expect(spotModeToRxMode('CW', 7_030_000, 'CWU')).toBe('CWU');
    expect(spotModeToRxMode('CW', 7_030_000, 'CWL')).toBe('CWL');
  });

  it('maps data modes to DIGU and RTTY to DIGL', () => {
    expect(spotModeToRxMode('FT8', 14_074_000)).toBe('DIGU');
    expect(spotModeToRxMode('JS8', 7_078_000)).toBe('DIGU');
    expect(spotModeToRxMode('RTTY', 14_080_000)).toBe('DIGL');
  });
});

describe('spotModeGroup', () => {
  it('classifies the common modes', () => {
    expect(spotModeGroup('CW')).toBe('CW');
    expect(spotModeGroup('SSB')).toBe('PHONE');
    expect(spotModeGroup('USB')).toBe('PHONE');
    expect(spotModeGroup('FM')).toBe('FM');
    expect(spotModeGroup('AM')).toBe('AM');
  });

  it('treats unknown / data modes as DIGITAL', () => {
    expect(spotModeGroup('FT8')).toBe('DIGITAL');
    expect(spotModeGroup('RTTY')).toBe('DIGITAL');
    expect(spotModeGroup('')).toBe('DIGITAL');
  });
});

describe('freqHzToBand', () => {
  it('maps frequencies to amateur bands', () => {
    expect(freqHzToBand(7_120_000)).toBe('40m');
    expect(freqHzToBand(14_074_000)).toBe('20m');
    expect(freqHzToBand(28_400_000)).toBe('10m');
    expect(freqHzToBand(146_520_000)).toBe('2m');
  });

  it('returns null outside every band', () => {
    expect(freqHzToBand(9_000_000)).toBeNull();
    expect(freqHzToBand(100_000)).toBeNull();
  });
});

describe('spotIsQrt', () => {
  it('detects QRT / closing comments', () => {
    expect(spotIsQrt(spot({ comments: 'Going QRT, thanks all' }))).toBe(true);
    expect(spotIsQrt(spot({ comments: 'QSY to 40m' }))).toBe(true);
    expect(spotIsQrt(spot({ comments: 'packing up' }))).toBe(true);
  });

  it('leaves ordinary comments alone', () => {
    expect(spotIsQrt(spot({ comments: 'Strong signal into EU' }))).toBe(false);
    expect(spotIsQrt(spot({ comments: null }))).toBe(false);
  });
});

describe('applySpotSettingsFilters', () => {
  const now = Date.parse('2026-06-14T12:00:00Z');

  it('passes everything through with default settings (except QRT, hidden by default)', () => {
    const list = [spot({ freqHz: 14_074_000 }), spot({ freqHz: 7_120_000, mode: 'CW' })];
    expect(applySpotSettingsFilters(list, settings({}), now)).toHaveLength(2);
  });

  it('filters by band allow-list', () => {
    const list = [spot({ freqHz: 14_074_000 }), spot({ freqHz: 7_120_000 })];
    const out = applySpotSettingsFilters(list, settings({ bands: ['20M'] }), now);
    expect(out).toHaveLength(1);
    expect(out[0]!.freqHz).toBe(14_074_000);
  });

  it('filters by mode group', () => {
    const list = [spot({ mode: 'CW' }), spot({ mode: 'FT8' }), spot({ mode: 'SSB' })];
    const out = applySpotSettingsFilters(list, settings({ modes: ['CW', 'DIGITAL'] }), now);
    expect(out.map((s) => s.mode)).toEqual(['CW', 'FT8']);
  });

  it('hides QRT spots when hideQrt is on, keeps them when off', () => {
    const list = [spot({ comments: 'QRT now' }), spot({ comments: 'CQ' })];
    expect(applySpotSettingsFilters(list, settings({ hideQrt: true }), now)).toHaveLength(1);
    expect(applySpotSettingsFilters(list, settings({ hideQrt: false }), now)).toHaveLength(2);
  });

  it('drops spots older than maxAgeMinutes', () => {
    const list = [
      spot({ spotTime: '2026-06-14T11:58:00' }), // 2 min old
      spot({ spotTime: '2026-06-14T11:30:00' }), // 30 min old
    ];
    const out = applySpotSettingsFilters(list, settings({ maxAgeMinutes: 10 }), now);
    expect(out).toHaveLength(1);
    expect(out[0]!.spotTime).toBe('2026-06-14T11:58:00');
  });

  it('collapses to the newest spot per activator when latestPerActivator is on', () => {
    const list = [
      spot({ activator: 'W1AW', spotTime: '2026-06-14T12:00:00' }),
      spot({ activator: 'W1AW', spotTime: '2026-06-14T11:00:00' }),
      spot({ activator: 'K2ABC', spotTime: '2026-06-14T11:30:00' }),
    ];
    const out = applySpotSettingsFilters(list, settings({ latestPerActivator: true }), now);
    expect(out).toHaveLength(2);
    expect(out.filter((s) => s.activator === 'W1AW')).toHaveLength(1);
    expect(out.find((s) => s.activator === 'W1AW')!.spotTime).toBe('2026-06-14T12:00:00');
  });
});
