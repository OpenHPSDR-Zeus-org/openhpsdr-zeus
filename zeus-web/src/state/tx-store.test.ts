// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it } from 'vitest';

import { useTxStore, type TxMeters } from './tx-store';

const VALID_METERS: TxMeters = {
  fwdWatts: 10,
  refWatts: 0.2,
  swr: 1.1,
  micPk: -12,
  micAv: -24,
  eqPk: -11,
  eqAv: -23,
  lvlrPk: -10,
  lvlrAv: -22,
  lvlrGr: 3,
  cfcPk: -9,
  cfcAv: -21,
  cfcGr: 2,
  compPk: -8,
  compAv: -20,
  alcPk: -7,
  alcAv: -19,
  alcGr: 4,
  outPk: -6,
  outAv: -18,
};

function resetTransientMeters(): void {
  useTxStore.getState().setMeters({
    ...VALID_METERS,
    fwdWatts: 0,
    refWatts: 0,
    swr: 1,
    micPk: -Infinity,
    micAv: -Infinity,
    eqPk: -Infinity,
    eqAv: -Infinity,
    lvlrPk: -Infinity,
    lvlrAv: -Infinity,
    lvlrGr: 0,
    cfcPk: -Infinity,
    cfcAv: -Infinity,
    cfcGr: 0,
    compPk: -Infinity,
    compAv: -Infinity,
    alcPk: -Infinity,
    alcAv: -Infinity,
    alcGr: 0,
    outPk: -Infinity,
    outAv: -Infinity,
  });
  useTxStore.setState({
    micDbfs: -100,
    rxDbm: -160,
    paTempC: null,
    psFeedbackLevel: 0,
    psCorrectionDb: 0,
    psCalState: 0,
    psCorrecting: false,
    psMaxTxEnvelope: 0,
  });
}

afterEach(resetTransientMeters);

describe('useTxStore meter ingress', () => {
  it('normalizes malformed TX meter frames to quiet meter sentinels', () => {
    useTxStore.getState().setMeters({
      ...VALID_METERS,
      fwdWatts: NaN,
      refWatts: -5,
      swr: Infinity,
      micPk: NaN,
      micAv: Infinity,
      eqPk: -Infinity,
      lvlrGr: -3,
      cfcGr: NaN,
      compPk: Infinity,
      alcGr: -Infinity,
      outPk: NaN,
    });

    const s = useTxStore.getState();
    expect(s.fwdWatts).toBe(0);
    expect(s.refWatts).toBe(0);
    expect(s.swr).toBe(1);
    expect(s.wdspMicPk).toBe(-Infinity);
    expect(s.micAv).toBe(-Infinity);
    expect(s.eqPk).toBe(-Infinity);
    expect(s.lvlrGr).toBe(0);
    expect(s.cfcGr).toBe(0);
    expect(s.compPk).toBe(-Infinity);
    expect(s.alcGr).toBe(0);
    expect(s.outPk).toBe(-Infinity);
    expect(s.outAv).toBe(VALID_METERS.outAv);
  });

  it('normalizes standalone mic, RX, PA temperature, and PureSignal meters', () => {
    const store = useTxStore.getState();
    store.setMicDbfs(NaN);
    store.setRxDbm(Infinity);
    store.setPaTempC(-Infinity);
    store.setPsMeters({
      feedbackLevel: Infinity,
      correctionDb: NaN,
      calState: -3,
      correcting: true,
      maxTxEnvelope: -1,
    });

    const s = useTxStore.getState();
    expect(s.micDbfs).toBe(-100);
    expect(s.rxDbm).toBe(-160);
    expect(s.paTempC).toBeNull();
    expect(s.psFeedbackLevel).toBe(0);
    expect(s.psCorrectionDb).toBe(0);
    expect(s.psCalState).toBe(0);
    expect(s.psCorrecting).toBe(true);
    expect(s.psMaxTxEnvelope).toBe(0);
  });
});
