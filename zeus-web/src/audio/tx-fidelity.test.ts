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
  psEnabled: true,
  psCorrecting: true,
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

  it('does not judge voice fidelity during tune carrier', () => {
    const a = analyzeTxFidelity({ ...BASE, moxOn: false, tunOn: true });
    expect(a.state).toBe('tune');
    expect(a.score).toBe(0);
  });
});
