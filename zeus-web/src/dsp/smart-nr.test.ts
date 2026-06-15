// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { NR_CONFIG_DEFAULT } from '../api/client';
import {
  adaptSmartNrToDspCapabilities,
  analyzeSmartNrCondition,
  recommendSmartNr,
  shapeSmartNrRecommendation,
} from './smart-nr';

const WIDTH = 256;
const NOISE_DB = -110;

function floor(db = NOISE_DB): Float32Array {
  return new Float32Array(WIDTH).fill(db + 3);
}

function noise(): Float32Array {
  return new Float32Array(WIDTH).fill(NOISE_DB);
}

function confidence(value = 0): Float32Array {
  return new Float32Array(WIDTH).fill(value);
}

describe('smart NR supervisor', () => {
  it('classifies weak sparse narrow signals', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 14;
    const c = analyzeSmartNrCondition(spec, floor())!;
    expect(c.hasSignal).toBe(true);
    expect(c.weakSparse).toBe(true);
    expect(c.denseNoise).toBe(false);
  });

  it('uses coherent ridges as weak-signal evidence when confidence is available', () => {
    const spec = noise();
    const conf = confidence();
    spec[119] = NOISE_DB + 13;
    spec[120] = NOISE_DB + 15;
    spec[121] = NOISE_DB + 13;
    conf[119] = 0.78;
    conf[120] = 0.86;
    conf[121] = 0.78;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
    })!;

    expect(rec.condition.weakSparse).toBe(true);
    expect(rec.condition.coherentRidgeCount).toBe(1);
    expect(rec.condition.widestCoherentRunBins).toBe(3);
    expect(rec.condition.coherentPeakCount).toBe(1);
    expect(rec.nr.nrMode).toBe('Sbnr');
  });

  it('promotes sustained coherent subthreshold ridges for weak-signal NR', () => {
    const spec = noise();
    const conf = confidence();
    spec[119] = NOISE_DB + 9.4;
    spec[120] = NOISE_DB + 9.8;
    spec[121] = NOISE_DB + 9.2;
    conf[119] = 0.74;
    conf[120] = 0.86;
    conf[121] = 0.73;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
    })!;

    expect(rec.condition.maxSnrDb).toBeLessThan(8);
    expect(rec.condition.coherentSubthresholdSignal).toBe(true);
    expect(rec.condition.hasSignal).toBe(true);
    expect(rec.condition.weakSparse).toBe(true);
    expect(rec.nr.nrMode).toBe('Sbnr');
  });

  it('uses RX chain telemetry to preserve faint sparse weak-signal NR decisions', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 8;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'CWU',
      rx: {
        signalDbm: -118,
        adcHeadroomDb: 18,
        agcGain: 38,
      },
    })!;

    expect(rec.condition.rxAssistedWeakSignal).toBe(true);
    expect(rec.condition.hasSignal).toBe(true);
    expect(rec.condition.weakSparse).toBe(true);
    expect(rec.nr.nrMode).toBe('Sbnr');
  });

  it('does not promote low-confidence subthreshold energy as a signal', () => {
    const spec = noise();
    const conf = confidence();
    spec[119] = NOISE_DB + 9.4;
    spec[120] = NOISE_DB + 9.8;
    spec[121] = NOISE_DB + 9.2;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
    })!;

    expect(rec.condition.coherentSubthresholdSignal).toBe(false);
    expect(rec.condition.hasSignal).toBe(false);
    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.nr.nrMode).toBe('Off');
  });

  it('uses RX and AGC telemetry to recover sparse sub-6 dB weak-signal copy', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 8;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
      rx: {
        signalDbm: -123,
        adcHeadroomDb: 18,
        agcGain: 42,
      },
    })!;

    expect(rec.condition.maxSnrDb).toBeLessThan(6);
    expect(rec.condition.rxAssistedWeakSignal).toBe(true);
    expect(rec.condition.weakSparse).toBe(true);
    expect(rec.nr.nrMode).toBe('Sbnr');
    expect(rec.nr.nr4ReductionAmount).toBe(7);
    expect(rec.nr.nr4WhiteningFactor).toBe(10);
    expect(rec.reason).toContain('Weak-signal assist');
  });

  it('uses low-artifact NR2 for RX-assisted weak SSB copy', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 8;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
      rx: {
        signalDbm: -121,
        adcHeadroomDb: 20,
        agcGain: 38,
      },
    })!;

    expect(rec.condition.rxAssistedWeakSignal).toBe(true);
    expect(rec.nr.nrMode).toBe('Emnr');
    expect(rec.nr.emnrPost2Run).toBe(true);
    expect(rec.nr.emnrPost2Factor).toBe(10);
    expect(rec.nr.emnrPost2Nlevel).toBe(10);
    expect(rec.nr.nbMode).toBe('Off');
  });

  it('reports coherent subthreshold SSB as a weak-signal NR2 profile', () => {
    const spec = noise();
    const conf = confidence();
    spec[119] = NOISE_DB + 9.4;
    spec[120] = NOISE_DB + 9.9;
    spec[121] = NOISE_DB + 9.3;
    conf[119] = 0.72;
    conf[120] = 0.84;
    conf[121] = 0.72;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'LSB',
    })!;

    expect(rec.condition.maxSnrDb).toBeLessThan(8);
    expect(rec.condition.coherentSubthresholdSignal).toBe(true);
    expect(rec.condition.weakSparse).toBe(true);
    expect(rec.nr.nrMode).toBe('Emnr');
    expect(rec.nr.emnrPost2Factor).toBe(12);
    expect(rec.nr.emnrPost2Nlevel).toBe(12);
    expect(rec.reason).toContain('coherent weak-signal');
    expect(rec.reason).toContain('subthreshold ridge');
  });

  it('recommends NR4/SBNR for weak CW and digital ridges', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 14;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'CWU',
    })!;
    expect(rec.nr.nrMode).toBe('Sbnr');
    expect(rec.nr.nr4ReductionAmount).toBe(8);
    expect(rec.nr.nr4WhiteningFactor).toBe(8);
    expect(rec.nr.nbMode).toBe('Off');
  });

  it('preserves the operator NR4 output rescale instead of stamping volume', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 14;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT, nr4NoiseRescale: 1.25 },
      mode: 'CWU',
    })!;

    expect(rec.nr.nr4NoiseRescale).toBe(1.25);
  });

  it('recommends NR2/EMNR for dense noisy SSB conditions', () => {
    const spec = noise();
    for (let i = 64; i < 192; i++) spec[i] = NOISE_DB + 12;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;
    expect(rec.condition.denseNoise).toBe(true);
    expect(rec.nr.nrMode).toBe('Emnr');
    expect(rec.nr.emnrAeRun).toBe(true);
    expect(rec.nr.emnrPost2Run).toBe(true);
    expect(rec.nr.snbEnabled).toBe(true);
  });

  it('uses low-artifact NR2 for live-diagnostic coherent SSB copy assist', () => {
    const spec = noise();
    const conf = confidence();
    for (let i = 64; i < 164; i += 4) {
      spec[i] = NOISE_DB + 32;
      conf[i] = 0.86;
    }

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;

    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.condition.denseNoise).toBe(false);
    expect(rec.condition.tonalInterference).toBe(false);
    expect(rec.condition.coherentCopySignal).toBe(true);
    expect(rec.nr.nrMode).toBe('Emnr');
    expect(rec.nr.emnrPost2Factor).toBe(11);
    expect(rec.nr.emnrPost2Nlevel).toBe(11);
    expect(rec.nr.nbMode).toBe('Off');
    expect(rec.nr.snbEnabled).toBe(false);
    expect(rec.reason).toContain('copy-assist');
  });

  it('treats spread coherent SSB peaks as copy assist instead of a notch-only case', () => {
    const spec = noise();
    const conf = confidence();
    for (let i = 72; i < 144; i += 4) {
      spec[i] = NOISE_DB + 29;
      conf[i] = 0.86;
    }

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;

    expect(rec.condition.coherentPeakCount).toBe(18);
    expect(rec.condition.coherentOccupancy6).toBeGreaterThan(0.06);
    expect(rec.condition.tonalInterference).toBe(false);
    expect(rec.condition.coherentCopySignal).toBe(true);
    expect(rec.nr.nrMode).toBe('Emnr');
    expect(rec.nr.anfEnabled).toBe(false);
    expect(rec.nr.nbpNotchesEnabled).toBe(false);
    expect(rec.reason).toContain('copy-assist');
  });

  it('does not mistake a lone low-confidence spike for a weak signal or tone', () => {
    const spec = noise();
    const conf = confidence();
    spec[120] = NOISE_DB + 25;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;

    expect(rec.condition.hasSignal).toBe(true);
    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.condition.impulsiveNoise).toBe(false);
    expect(rec.condition.tonalInterference).toBe(false);
    expect(rec.condition.coherentRidgeCount).toBe(0);
    expect(rec.condition.isolatedHotBinCount).toBe(1);
    expect(rec.nr.nrMode).toBe('Off');
    expect(rec.nr.nbMode).toBe('Off');
    expect(rec.nr.anfEnabled).toBe(false);
  });

  it('classifies broad coherent raised noise as dense instead of weak-sparse', () => {
    const spec = noise();
    const conf = confidence(0.82);
    for (let i = 72; i < 184; i++) spec[i] = NOISE_DB + 11;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;

    expect(rec.condition.denseNoise).toBe(true);
    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.condition.widestCoherentRunBins).toBe(112);
    expect(rec.nr.nrMode).toBe('Emnr');
  });

  it('treats non-coherent bright bins as impulsive noise, not weak-signal NR', () => {
    const spec = noise();
    const conf = confidence();
    for (let i = 40; i < 160; i += 12) spec[i] = NOISE_DB + 25;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT, nbThreshold: 20 },
      mode: 'USB',
    })!;
    expect(rec.condition.impulsiveNoise).toBe(true);
    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.nr.nrMode).toBe('Off');
    expect(rec.nr.nbMode).toBe('Nb2');
    expect(rec.nr.snbEnabled).toBe(true);
    expect(rec.nr.nbThreshold).toBe(16);
  });

  it('ignores non-finite bins when classifying Smart NR conditions', () => {
    const spec = noise();
    const f = floor();
    const conf = confidence(0.8);
    spec[80] = Infinity;
    spec[90] = NaN;
    spec[100] = -Infinity;
    f[110] = NaN;
    conf[80] = Infinity;

    const rec = recommendSmartNr({
      spectrum: spec,
      floor: f,
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
    })!;

    expect(rec.condition.maxSnrDb).toBe(0);
    expect(rec.condition.hasSignal).toBe(false);
    expect(rec.condition.weakSparse).toBe(false);
    expect(rec.condition.denseNoise).toBe(false);
    expect(rec.condition.impulsiveNoise).toBe(false);
    expect(rec.nr.nrMode).toBe('Off');
  });

  it('uses notch helpers for strong sparse tonal interference', () => {
    const spec = noise();
    spec[90] = NOISE_DB + 35;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'USB',
    })!;
    expect(rec.condition.tonalInterference).toBe(true);
    expect(rec.nr.anfEnabled).toBe(true);
    expect(rec.nr.nbpNotchesEnabled).toBe(true);
  });

  it('keeps clean carrier modes out of NR when the spectrum is quiet', () => {
    const rec = recommendSmartNr({
      spectrum: noise(),
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'AM',
    })!;
    expect(rec.nr.nrMode).toBe('Off');
    expect(rec.nr.snbEnabled).toBe(false);
    expect(rec.nr.nbMode).toBe('Off');
  });

  it('falls back to a global floor when the display estimator is cold', () => {
    const spec = noise();
    spec[128] = NOISE_DB + 20;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: null,
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'DIGU',
    })!;
    expect(rec.condition.hasSignal).toBe(true);
    expect(rec.nr.nrMode).toBe('Sbnr');
  });

  it('shapes recommendations with operator Smart NR tuning', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 14;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'CWU',
    })!;

    const conservative = shapeSmartNrRecommendation(rec, {
      aggressiveness: 15,
      autoBlankerEnabled: true,
      autoNotchEnabled: true,
      maxBlankerThreshold: 12,
    });
    const aggressive = shapeSmartNrRecommendation(rec, {
      aggressiveness: 100,
      autoBlankerEnabled: true,
      autoNotchEnabled: true,
      maxBlankerThreshold: 12,
    });

    expect(conservative.nrMode).toBe('Off');
    expect(aggressive.nrMode).toBe('Sbnr');
    expect(aggressive.nr4ReductionAmount).toBe(13);
    expect(aggressive.nr4WhiteningFactor).toBe(10);
    expect(aggressive.nr4PostFilterThreshold).toBe(-5);
  });

  it('shapes malformed tuning without emitting non-finite WDSP parameters', () => {
    const spec = noise();
    const conf = confidence();
    for (let i = 40; i < 160; i += 12) spec[i] = NOISE_DB + 25;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT, nbThreshold: Number.NaN },
      mode: 'USB',
    })!;

    const shaped = shapeSmartNrRecommendation(rec, {
      aggressiveness: Number.NaN,
      autoBlankerEnabled: true,
      autoNotchEnabled: true,
      maxBlankerThreshold: Number.NaN,
    });

    expect(shaped.nbMode).toBe('Nb2');
    expect(shaped.nbThreshold).toBe(16);
    expect(Number.isFinite(shaped.nbThreshold)).toBe(true);
  });

  it('honors disabled blanker and notch helpers while shaping', () => {
    const spec = noise();
    const conf = confidence();
    for (let i = 40; i < 160; i += 12) spec[i] = NOISE_DB + 25;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      confidence: conf,
      current: { ...NR_CONFIG_DEFAULT, nbThreshold: 20 },
      mode: 'USB',
    })!;

    const shaped = shapeSmartNrRecommendation(rec, {
      aggressiveness: 70,
      autoBlankerEnabled: false,
      autoNotchEnabled: false,
      maxBlankerThreshold: 12,
    });

    expect(shaped.nbMode).toBe('Off');
    expect(shaped.snbEnabled).toBe(false);
    expect(shaped.anfEnabled).toBe(false);
    expect(shaped.nbpNotchesEnabled).toBe(false);
  });

  it('downgrades NR4 recommendations to NR2 when SBNR exports are unavailable', () => {
    const spec = noise();
    spec[120] = NOISE_DB + 14;
    const rec = recommendSmartNr({
      spectrum: spec,
      floor: floor(),
      current: { ...NR_CONFIG_DEFAULT },
      mode: 'CWU',
    })!;
    const shaped = shapeSmartNrRecommendation(rec, {
      aggressiveness: 100,
      autoBlankerEnabled: true,
      autoNotchEnabled: true,
      maxBlankerThreshold: 12,
    });
    const adapted = adaptSmartNrToDspCapabilities(shaped, {
      wdspActive: true,
      wdspEmnrPost2Available: true,
      wdspNr4SbnrAvailable: false,
    });

    expect(shaped.nrMode).toBe('Sbnr');
    expect(adapted.nr.nrMode).toBe('Emnr');
    expect(adapted.nr.emnrAeRun).toBe(true);
    expect(adapted.nr.emnrPost2Run).toBe(true);
    expect(adapted.capabilityLimited).toBe(true);
    expect(adapted.capabilityRecommendation).toContain('NR4/SBNR unavailable');
  });

  it('keeps EMNR core but disables post2 when those exports are unavailable', () => {
    const adapted = adaptSmartNrToDspCapabilities(
      { ...NR_CONFIG_DEFAULT, nrMode: 'Emnr', emnrPost2Run: true, emnrAeRun: true },
      {
        wdspActive: true,
        wdspEmnrPost2Available: false,
        wdspNr4SbnrAvailable: true,
      },
    );

    expect(adapted.nr.nrMode).toBe('Emnr');
    expect(adapted.nr.emnrPost2Run).toBe(false);
    expect(adapted.nr.emnrAeRun).toBe(true);
    expect(adapted.capabilityLimited).toBe(true);
    expect(adapted.capabilityRecommendation).toContain('post2');
  });
});
