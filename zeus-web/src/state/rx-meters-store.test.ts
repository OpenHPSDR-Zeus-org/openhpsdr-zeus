// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it } from 'vitest';

import { useRxMetersStore, type RxMeters } from './rx-meters-store';

const QUIET: RxMeters = {
  signalPk: -Infinity,
  signalAv: -Infinity,
  adcPk: -Infinity,
  adcAv: -Infinity,
  agcGain: 0,
  agcEnvPk: -Infinity,
  agcEnvAv: -Infinity,
};

afterEach(() => {
  useRxMetersStore.getState().setMeters(QUIET);
});

describe('useRxMetersStore meter ingress', () => {
  it('normalizes malformed RX meter frames to quiet meter sentinels', () => {
    useRxMetersStore.getState().setMeters({
      signalPk: NaN,
      signalAv: Infinity,
      adcPk: -Infinity,
      adcAv: NaN,
      agcGain: Infinity,
      agcEnvPk: Number.NaN,
      agcEnvAv: -122,
    });

    const s = useRxMetersStore.getState();
    expect(s.signalPk).toBe(-Infinity);
    expect(s.signalAv).toBe(-Infinity);
    expect(s.adcPk).toBe(-Infinity);
    expect(s.adcAv).toBe(-Infinity);
    expect(s.agcGain).toBe(0);
    expect(s.agcEnvPk).toBe(-Infinity);
    expect(s.agcEnvAv).toBe(-122);
  });
});
