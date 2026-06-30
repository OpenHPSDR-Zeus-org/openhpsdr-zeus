// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { analyzeTxFidelity, type TxFidelitySnapshot } from './tx-fidelity';

const BASE: TxFidelitySnapshot = {
  moxOn: true,
  tunOn: false,
  txMonitorEnabled: false,
  micDbfs: -24,
  wdspMicPk: -10,
  micAv: -21,
  lvlrGr: 4,
  cfcGr: 2,
  compPk: -10,
  compAv: -21,
  alcGr: 5,
  outPk: -8,
  outAv: -19,
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
    expect(a.crestStatus).toBe('controlled');
    expect(a.outCrestDb).toBeCloseTo(11, 1);
  });

  it('flags under-driven audio', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -36, outPk: -24, outAv: -37, alcGr: 0.2 });
    expect(a.state).toBe('under');
    expect(a.label).toBe('Under-driven');
    expect(a.detail).toContain('Mic peak is low');
    expect(a.detail).toContain('TX density is below clean profile target');
    expect(a.recommendation).toBe('Raise mic gain toward -12 to -6 dBFS peaks');
    expect(a.actionTone).toBe('raise');
    expect(a.densityStatus).toBe('thin');
  });

  it('detects high-crest speech as too open for dense profiles', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -12,
      micAv: -34,
      outPk: -9,
      outAv: -31,
      targetSpectralDensity: 100,
    });
    expect(a.state).toBe('under');
    expect(a.crestStatus).toBe('open');
    expect(a.detail).toContain('Crest factor is too open');
    expect(a.recommendation).toBe('Add controlled speech density before adding RF drive');
  });

  it('flags hard limiting before clipping', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -2.5, alcGr: 12 });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.detail).toContain('ALC is limiting hard');
    expect(a.recommendation).toBe('Lower mic gain until peaks stay below -6 dBFS');
    expect(a.actionTone).toBe('reduce');
  });

  it('keeps recommendations aligned with sanitized malformed TX telemetry', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -2.5,
      alcGr: 12,
      lvlrGr: -5,
      cfcGr: -2,
      swr: Infinity,
      psFeedbackLevel: Infinity,
    });

    expect(a.state).toBe('hot');
    expect(a.swr).toBe(1);
    expect(a.psFeedbackLevel).toBeNull();
    expect(a.lvlrGr).toBe(0);
    expect(a.cfcGr).toBe(0);
    expect(a.recommendation).toBe('Lower mic gain until peaks stay below -6 dBFS');
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

  it('prioritizes VST chain headroom when plugin output is pinned', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -6,
      micAv: -18,
      lvlrGr: 3,
      cfcGr: 0,
      compPk: -6,
      compAv: -17,
      alcGr: 3,
      outPk: -6.1,
      outAv: -17,
      audioSuiteMode: 'vst',
      audioSuiteBypassed: false,
      vstEngineActive: true,
      audioSuiteInputDbfs: -14.4,
      audioSuiteOutputDbfs: -0.1,
    });

    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.audioSuiteLabel).toBe('VST chain');
    expect(a.audioSuiteOutputDbfs).toBeCloseTo(-0.1, 1);
    expect(a.detail).toContain('VST chain output has almost no headroom');
    expect(a.detail).toContain('VST chain is adding excessive gain');
    expect(a.recommendation).toBe('Lower VST/plugin output trim before raising mic or drive');
    expect(a.actionTone).toBe('reduce');
    expect(a.densityStatus).toBe('forced');
  });

  it('treats a full-scale VST chain meter as a clipping risk', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -6,
      outPk: -6,
      audioSuiteMode: 'vst',
      audioSuiteBypassed: false,
      vstEngineActive: true,
      audioSuiteInputDbfs: -14,
      audioSuiteOutputDbfs: 0,
    });

    expect(a.state).toBe('clip');
    expect(a.detail).toBe('VST chain output is reaching full scale.');
    expect(a.recommendation).toBe('Lower VST/plugin output trim before raising mic or drive');
    expect(a.actionTone).toBe('protect');
  });

  it('ignores stale Audio Suite meter values while master bypassed', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      audioSuiteMode: 'vst',
      audioSuiteBypassed: true,
      vstEngineActive: true,
      audioSuiteInputDbfs: -14,
      audioSuiteOutputDbfs: 0,
    });

    expect(a.state).toBe('sweet');
    expect(a.audioSuiteOutputDbfs).toBeNull();
    expect(a.recommendation).toBe('Hold levels; PureSignal is correcting the PA');
  });

  it('matches live density against the station profile target', () => {
    const a = analyzeTxFidelity({ ...BASE, targetSpectralDensity: 100 });
    expect(a.state).toBe('under');
    expect(a.targetSpectralDensity).toBe(100);
    expect(a.cleanSpectralDensityTarget).toBe(84);
    expect(a.liveSpectralDensity).toBeLessThan(80);
    expect(a.detail).toContain('TX density is below clean profile target');
    expect(a.recommendation).toBe('Increase mic gain or profile density before adding drive');
  });

  it('lets max-density targets go green inside the clean dynamics windows', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      targetSpectralDensity: 100,
      wdspMicPk: -6,
      micAv: -16,
      outPk: -4,
      outAv: -11,
      alcGr: 6,
      lvlrGr: 6,
      cfcGr: 5,
    });

    expect(a.state).toBe('sweet');
    expect(a.cleanSpectralDensityTarget).toBe(84);
    expect(a.densityStatus).toBe('matched');
    expect(a.densityFit).toBeGreaterThanOrEqual(80);
    expect(a.tuningMetrics.find((m) => m.id === 'dens')?.status).toBe('met');
    expect(a.detail).toContain('All live TX fidelity targets are green');
  });

  it('flags forced density from an over-compressed speech chain', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -8,
      micAv: -13,
      outAv: -12,
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

  it('flags pinched crest factor even before clipping', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      wdspMicPk: -8,
      micAv: -12,
      outPk: -7,
      outAv: -11,
      alcGr: 5,
      lvlrGr: 4,
      cfcGr: 4,
    });
    expect(a.state).toBe('hot');
    expect(a.crestStatus).toBe('pinched');
    expect(a.detail).toContain('Crest factor is pinched');
    expect(a.recommendation).toBe('Reduce CFC density before raising drive');
  });

  it('attributes pinched crest to the compressor when output crest is unavailable', () => {
    const a = analyzeTxFidelity({
      ...BASE,
      outPk: -Infinity,
      outAv: -Infinity,
      compPk: -7,
      compAv: -10,
      alcGr: 4,
      lvlrGr: 4,
      cfcGr: 1,
    });
    expect(a.state).toBe('hot');
    expect(a.crestStatus).toBe('pinched');
    expect(a.compDbfs).toBeCloseTo(-7, 1);
    expect(a.compCrestDb).toBeCloseTo(3, 1);
    expect(a.detail).toContain('Compressor crest is pinched');
    expect(a.recommendation).toBe('Reduce compressor gain before raising drive');
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
    expect(a.recommendation).toBe('Use MOX or Preview for voice-chain metering');
  });
});
