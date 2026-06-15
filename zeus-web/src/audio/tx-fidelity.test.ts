// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { analyzeTxFidelity, type TxFidelitySnapshot } from './tx-fidelity';

const BASE: TxFidelitySnapshot = {
  moxOn: true,
  tunOn: false,
  txMonitorEnabled: false,
  micDbfs: -24,
  wdspMicPk: -10,
  lvlrGr: 4,
  cfcGr: 2,
  alcGr: 5,
  outPk: -8,
  swr: 1.1,
  psEnabled: true,
  psCorrecting: true,
  psFeedbackLevel: 150,
  psCalState: 0,
  psCalibrationStalled: false,
};

describe('analyzeTxFidelity', () => {
  it('recognizes the broadcast sweet spot', () => {
    const a = analyzeTxFidelity(BASE);
    expect(a.state).toBe('sweet');
    expect(a.label).toBe('Broadcast sweet spot');
    expect(a.score).toBeGreaterThanOrEqual(90);
    expect(a.detail).toContain('PureSignal correcting');
    expect(a.recommendation).toBe('Hold levels; PureSignal is correcting the PA');
    expect(a.actionTone).toBe('neutral');
    expect(a.liveSpectralDensity).toBeGreaterThanOrEqual(60);
    expect(a.densityFit).toBeGreaterThanOrEqual(80);
    expect(a.densityStatus).toBe('matched');
  });

  it('flags under-driven audio', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -36, alcGr: 0.2 });
    expect(a.state).toBe('under');
    expect(a.label).toBe('Under-driven');
    expect(a.detail).toContain('Mic peak is low');
    expect(a.detail).toContain('TX density is below profile target');
    expect(a.recommendation).toBe('Raise mic gain toward -12 to -6 dBFS peaks');
    expect(a.actionTone).toBe('raise');
    expect(a.densityStatus).toBe('thin');
  });

  it('flags hard limiting before clipping', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -2.5, alcGr: 12 });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.detail).toContain('ALC is limiting hard');
    expect(a.recommendation).toBe('Lower mic gain until peaks stay below -6 dBFS');
    expect(a.actionTone).toBe('reduce');
  });

  it('treats full-scale mic or output as clipping risk', () => {
    const mic = analyzeTxFidelity({ ...BASE, wdspMicPk: 0 });
    const output = analyzeTxFidelity({ ...BASE, outPk: 0 });
    expect(mic.state).toBe('clip');
    expect(output.state).toBe('clip');
    expect(mic.recommendation).toBe('Back down mic gain or drive now');
    expect(mic.actionTone).toBe('protect');
  });

  it('flags tight TX output headroom before hard clipping', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -9, alcGr: 4, outPk: -0.6 });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.outDbfs).toBeCloseTo(-0.6, 1);
    expect(a.detail).toContain('TX output has almost no headroom');
    expect(a.recommendation).toBe('Reduce drive or ALC max gain for TX output headroom');
  });

  it('matches live density against the station profile target', () => {
    const a = analyzeTxFidelity({ ...BASE, targetSpectralDensity: 100 });
    expect(a.state).toBe('under');
    expect(a.targetSpectralDensity).toBe(100);
    expect(a.liveSpectralDensity).toBeLessThan(80);
    expect(a.detail).toContain('TX density is below profile target');
    expect(a.recommendation).toBe('Increase mic gain or profile density before adding drive');
  });

  it('flags forced density from an over-compressed speech chain', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -8,
      alcGr: 9.5,
      lvlrGr: 12,
      cfcGr: 9,
    });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.densityStatus).toBe('forced');
    expect(a.detail).toContain('Density is forced by compression');
    expect(a.recommendation).toBe('Lower mic gain or ALC max gain');
  });

  it('surfaces PureSignal feedback and calibration health', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      psCorrecting: false,
      psFeedbackLevel: 94,
      psCalibrationStalled: true,
    });
    expect(a.state).toBe('hot');
    expect(a.score).toBeLessThan(50);
    expect(a.psFeedbackLevel).toBe(94);
    expect(a.detail).toContain('PureSignal calibration stalled');
    expect(a.detail).toContain('PureSignal feedback 94 outside 128..181');
    expect(a.recommendation).toBe('Correct PureSignal feedback before increasing drive');
    expect(a.actionTone).toBe('protect');
  });

  it('warns on unsafe RF match even when audio dynamics look clean', () => {
    const a = analyzeTxFidelity({ ...BASE, swr: 3.2 });
    expect(a.state).toBe('hot');
    expect(a.detail).toContain('SWR protection risk');
    expect(a.recommendation).toBe('Stop RF and check antenna match');
  });

  it('does not judge voice fidelity during tune carrier', () => {
    const a = analyzeTxFidelity({ ...BASE, moxOn: false, tunOn: true });
    expect(a.state).toBe('tune');
    expect(a.score).toBe(0);
    expect(a.liveSpectralDensity).toBeNull();
    expect(a.densityFit).toBeNull();
    expect(a.recommendation).toBe('Use MOX or TX monitor for voice-chain metering');
  });
});
