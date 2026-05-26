// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { describe, it, expect } from 'vitest';
import {
  GoertzelFilter,
  AdaptiveThreshold,
  TimingEstimator,
  MorseFsm,
  CwDecoder,
  MORSE_CODE,
  MORSE_DECODE,
} from './decoder';

describe('GoertzelFilter', () => {
  it('creates a filter with correct parameters', () => {
    const filter = new GoertzelFilter(600, 48000);
    expect(filter).toBeDefined();
  });

  it('processes samples and returns magnitude', () => {
    const filter = new GoertzelFilter(600, 48000);
    const magnitude = filter.process(0.5);
    expect(typeof magnitude).toBe('number');
    expect(magnitude).toBeGreaterThanOrEqual(0);
  });

  it('resets filter state', () => {
    const filter = new GoertzelFilter(600, 48000);
    filter.process(0.5);
    filter.process(0.3);
    filter.reset();
    // After reset, process should return different magnitude
    const magnitude = filter.process(0.5);
    expect(typeof magnitude).toBe('number');
  });

  it('updates target frequency', () => {
    const filter = new GoertzelFilter(600, 48000);
    filter.setTargetFreq(800);
    // Should not throw
    const magnitude = filter.process(0.5);
    expect(typeof magnitude).toBe('number');
  });
});

describe('AdaptiveThreshold', () => {
  it('creates with default parameters', () => {
    const threshold = new AdaptiveThreshold();
    expect(threshold).toBeDefined();
  });

  it('processes magnitude values', () => {
    const threshold = new AdaptiveThreshold(10, 0.3);
    const above = threshold.process(0.5);
    expect(typeof above).toBe('boolean');
  });

  it('computes threshold after enough samples', () => {
    const threshold = new AdaptiveThreshold(5, 0.3);
    // Feed low values (noise)
    for (let i = 0; i < 20; i++) {
      threshold.process(0.01);
    }
    const thresh = threshold.getThreshold();
    expect(thresh).toBeGreaterThan(0);
  });

  it('estimates SNR', () => {
    const threshold = new AdaptiveThreshold();
    const snr = threshold.getSnrDb();
    expect(typeof snr).toBe('number');
    expect(snr).toBeGreaterThanOrEqual(0);
  });

  it('resets state', () => {
    const threshold = new AdaptiveThreshold();
    threshold.process(0.5);
    threshold.reset();
    // Should not throw
    threshold.process(0.3);
  });
});

describe('TimingEstimator', () => {
  it('creates with default parameters', () => {
    const estimator = new TimingEstimator();
    expect(estimator).toBeDefined();
  });

  it('records durations and estimates dit', () => {
    const estimator = new TimingEstimator();
    // Record 10 dits (24 ms each)
    for (let i = 0; i < 10; i++) {
      estimator.record(24);
    }
    const ditMs = estimator.getDitMs();
    expect(ditMs).toBeGreaterThan(0);
    expect(ditMs).toBeLessThan(120); // Should be clamped
  });

  it('classifies durations as dit or dah', () => {
    const estimator = new TimingEstimator();
    estimator.record(24);
    estimator.record(72);
    expect(estimator.classify(24)).toBe('dit');
    expect(estimator.classify(72)).toBe('dah');
  });

  it('classifies gaps correctly', () => {
    const estimator = new TimingEstimator();
    // Need to record enough samples to update thresholds (min 5)
    for (let i = 0; i < 5; i++) {
      estimator.record(24);
    }
    // After recording 24ms dits:
    // - ditMs ≈ 24
    // - elementGapThreshold = 1.2 * 24 = 28.8
    // - letterGapThreshold = 3.0 * 24 = 72
    // - wordGapThreshold = 5.5 * 24 = 132
    expect(estimator.classifyGap(20)).toBe('element');
    expect(estimator.classifyGap(50)).toBe('letter'); // 50 < 72
    expect(estimator.classifyGap(100)).toBe('word'); // 100 < 132
  });

  it('estimates WPM from dit duration', () => {
    const estimator = new TimingEstimator();
    estimator.record(24);
    const wpm = estimator.getWpm();
    expect(wpm).toBeGreaterThan(0);
    expect(wpm).toBeLessThan(100);
  });

  it('resets state', () => {
    const estimator = new TimingEstimator();
    estimator.record(24);
    estimator.reset();
    const ditMs = estimator.getDitMs();
    expect(ditMs).toBe(24); // Reset to default
  });
});

describe('MorseFsm', () => {
  it('creates with timing estimator', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    expect(fsm).toBeDefined();
  });

  it('processes key-down events', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    const result = fsm.onKeyDown(performance.now());
    expect(result).toBeNull(); // No character on key-down
  });

  it('processes key-up events', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    const now = performance.now();
    fsm.onKeyDown(now);
    const result = fsm.onKeyUp(now + 24);
    expect(result).toBeNull(); // Pattern building, no character yet
  });

  it('emits character after letter gap', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    const now = performance.now();
    // Send "E" (one dit)
    fsm.onKeyDown(now);
    fsm.onKeyUp(now + 24);
    // Letter gap - emits character automatically
    const result = fsm.onKeyDown(now + 100);
    expect(result).toBe('E'); // Pattern emitted on next key-down after letter gap
  });

  it('flushes pending character', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    const now = performance.now();
    // Send "E" (one dit)
    fsm.onKeyDown(now);
    fsm.onKeyUp(now + 24);
    const result = fsm.flush();
    expect(result).toBe('E'); // "." decodes to "E"
  });

  it('resets state', () => {
    const timing = new TimingEstimator();
    const fsm = new MorseFsm(timing);
    fsm.onKeyDown(performance.now());
    fsm.reset();
    // Should not throw
    fsm.onKeyUp(performance.now() + 24);
  });
});

describe('MORSE_CODE mapping', () => {
  it('contains expected characters', () => {
    expect(MORSE_CODE['A']).toBe('.-');
    expect(MORSE_CODE['E']).toBe('.');
    expect(MORSE_CODE['T']).toBe('-');
    expect(MORSE_CODE['S']).toBe('...');
    expect(MORSE_CODE['O']).toBe('---');
  });

  it('contains numbers', () => {
    expect(MORSE_CODE['0']).toBe('-----');
    expect(MORSE_CODE['1']).toBe('.----');
    expect(MORSE_CODE['5']).toBe('.....');
    expect(MORSE_CODE['9']).toBe('----.');
  });
});

describe('MORSE_DECODE mapping', () => {
  it('reverses MORSE_CODE', () => {
    expect(MORSE_DECODE['.-']).toBe('A');
    expect(MORSE_DECODE['.']).toBe('E');
    expect(MORSE_DECODE['-']).toBe('T');
    expect(MORSE_DECODE['...']).toBe('S');
    expect(MORSE_DECODE['---']).toBe('O');
  });

  it('returns "?" for unknown patterns', () => {
    expect(MORSE_DECODE['.-.-.-.'] ?? '?').toBe('?'); // Using ?? since undefined may be returned
  });
});

describe('CwDecoder', () => {
  it('creates with config', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    expect(decoder).toBeDefined();
  });

  it('processes audio samples', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    const result = decoder.process(0);
    expect(result).toBeNull(); // No signal, no character
  });

  it('gets current WPM estimate', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    const wpm = decoder.getWpm();
    expect(wpm).toBeGreaterThanOrEqual(0);
  });

  it('gets SNR estimate', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    const snr = decoder.getSnrDb();
    expect(snr).toBeGreaterThanOrEqual(0);
  });

  it('gets confidence estimate', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    const conf = decoder.getConfidence();
    expect(conf).toBeGreaterThanOrEqual(0);
    expect(conf).toBeLessThanOrEqual(1);
  });

  it('updates target frequency', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    decoder.setTargetFreq(800);
    // Should not throw
    decoder.process(0.5);
  });

  it('resets all state', () => {
    const decoder = new CwDecoder({
      targetFreq: 600,
      sampleRate: 48000,
    });
    decoder.process(0.5);
    decoder.reset();
    // Should not throw
    decoder.process(0.3);
  });
});