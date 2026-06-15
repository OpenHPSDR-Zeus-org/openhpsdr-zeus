// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  analyzeRxChain,
  preferredRxSignalDbm,
  type RxChainSnapshot,
} from './rx-chain-health';

const BASE: RxChainSnapshot = {
  signalPk: -73,
  signalAv: -78,
  adcPk: -18,
  adcAv: -30,
  agcGain: 6,
  agcEnvPk: -80,
  agcEnvAv: -86,
  fallbackDbm: -120,
};

describe('rx-chain-health', () => {
  it('prefers calibrated RxMetersV2 signal over legacy S-meter frames', () => {
    expect(preferredRxSignalDbm(BASE)).toEqual({
      dbm: -73,
      source: 'rx-meters-v2',
    });
  });

  it('falls back to the legacy S-meter until RxMetersV2 signal is live', () => {
    const preferred = preferredRxSignalDbm({
      ...BASE,
      signalPk: -Infinity,
      signalAv: -Infinity,
      fallbackDbm: -91,
    });
    expect(preferred).toEqual({ dbm: -91, source: 'legacy-rx-meter' });
  });

  it('recognizes a clean receiver chain with usable ADC headroom', () => {
    const a = analyzeRxChain(BASE);
    expect(a.state).toBe('optimized');
    expect(a.label).toBe('RX chain optimized');
    expect(a.score).toBeGreaterThanOrEqual(90);
    expect(a.adcHeadroomDb).toBe(18);
    expect(a.adcCrestDb).toBe(12);
    expect(a.agcEnvAv).toBe(-86);
    expect(a.agcEnvCrestDb).toBe(6);
    expect(a.signalSource).toBe('rx-meters-v2');
    expect(a.recommendation).toBe('Hold front-end settings');
    expect(a.actionTone).toBe('neutral');
  });

  it('flags ADC overload before weak signals get masked', () => {
    const a = analyzeRxChain({ ...BASE, adcPk: -0.7 });
    expect(a.state).toBe('overload');
    expect(a.label).toBe('ADC overload risk');
    expect(a.score).toBeLessThan(20);
    expect(a.detail).toContain('full scale');
    expect(a.recommendation).toBe('Add attenuation now');
    expect(a.actionTone).toBe('protect');
  });

  it('flags an under-filled ADC when a readable signal is wasting dynamic range', () => {
    const a = analyzeRxChain({ ...BASE, signalPk: -86, adcPk: -82 });
    expect(a.state).toBe('underfilled');
    expect(a.label).toBe('Front end under-filled');
    expect(a.detail).toContain('ADC is under-filled');
    expect(a.recommendation).toContain('Recover dynamic range');
    expect(a.actionTone).toBe('optimize');
  });

  it('uses ADC average to catch a readable signal with only transient peak fill', () => {
    const a = analyzeRxChain({
      ...BASE,
      signalPk: -86,
      adcPk: -38,
      adcAv: -92,
    });

    expect(a.state).toBe('underfilled');
    expect(a.detail).toContain('ADC average is under-filled');
    expect(a.adcCrestDb).toBe(54);
    expect(a.actionTone).toBe('optimize');
  });

  it('does not treat a low ADC average as under-filled when peak fill is healthy', () => {
    const a = analyzeRxChain({
      ...BASE,
      signalPk: -86,
      adcPk: -18,
      adcAv: -92,
    });

    expect(a.state).toBe('optimized');
    expect(a.detail).not.toContain('under-filled');
    expect(a.adcCrestDb).toBe(74);
  });

  it('classifies high-boost weak-signal copy as AGC-stressed', () => {
    const a = analyzeRxChain({ ...BASE, signalPk: -118, adcPk: -72, agcGain: 48 });
    expect(a.state).toBe('agc-stressed');
    expect(a.label).toBe('AGC stressed');
    expect(a.detail).toContain('heavy boost');
    expect(a.recommendation).toContain('Reduce attenuation');
    expect(a.actionTone).toBe('optimize');
  });
});
