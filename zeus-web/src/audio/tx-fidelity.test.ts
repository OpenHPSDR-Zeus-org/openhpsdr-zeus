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
  });

  it('flags under-driven audio', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -36, alcGr: 0.2 });
    expect(a.state).toBe('under');
    expect(a.label).toBe('Under-driven');
    expect(a.detail).toContain('Mic peak is low');
  });

  it('flags hard limiting before clipping', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -2.5, alcGr: 12 });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.detail).toContain('ALC is limiting hard');
  });

  it('treats full-scale mic or output as clipping risk', () => {
    const mic = analyzeTxFidelity({ ...BASE, wdspMicPk: 0 });
    const output = analyzeTxFidelity({ ...BASE, outPk: 0 });
    expect(mic.state).toBe('clip');
    expect(output.state).toBe('clip');
  });

  it('flags tight TX output headroom before hard clipping', () => {
    const a = analyzeTxFidelity({ ...BASE, wdspMicPk: -9, alcGr: 4, outPk: -0.6 });
    expect(a.state).toBe('hot');
    expect(a.label).toBe('Too hot');
    expect(a.outDbfs).toBeCloseTo(-0.6, 1);
    expect(a.detail).toContain('TX output has almost no headroom');
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
  });

  it('warns on unsafe RF match even when audio dynamics look clean', () => {
    const a = analyzeTxFidelity({ ...BASE, swr: 3.2 });
    expect(a.state).toBe('hot');
    expect(a.detail).toContain('SWR protection risk');
  });

  it('does not judge voice fidelity during tune carrier', () => {
    const a = analyzeTxFidelity({ ...BASE, moxOn: false, tunOn: true });
    expect(a.state).toBe('tune');
    expect(a.score).toBe(0);
  });
});
