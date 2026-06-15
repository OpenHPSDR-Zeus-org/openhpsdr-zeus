// SPDX-License-Identifier: GPL-2.0-or-later
//
// TX Fidelity must mount without an external-store render loop. React 19 +
// Zustand 5 treat selectors that return a new object every render as unstable
// snapshots; this test exercises the component path that used to throw the
// minified React #185 maximum-depth error in production builds.

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it } from 'vitest';
import { createElement } from 'react';

import { render, act } from './meters/__tests__/harness';
import { useTxStore } from '../state/tx-store';
import { TxFidelityAdvisor } from './TxFidelityAdvisor';

describe('TxFidelityAdvisor', () => {
  afterEach(() => {
    useTxStore.setState({
      moxOn: false,
      tunOn: false,
      txMonitorEnabled: false,
      micDbfs: -100,
      wdspMicPk: -Infinity,
      micAv: -Infinity,
      lvlrGr: 0,
      cfcGr: 0,
      compPk: -Infinity,
      compAv: -Infinity,
      alcGr: 0,
      outPk: -Infinity,
      outAv: -Infinity,
      swr: 1.0,
      psEnabled: false,
      psCorrecting: false,
      psFeedbackLevel: 0,
      psCalState: 0,
      psCalibrationStalled: false,
    });
  });

  it('renders and reacts to TX meter updates without re-entering render', () => {
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -10,
      micAv: -21,
      alcGr: 3,
      lvlrGr: 4,
      cfcGr: 2,
      compPk: -10,
      compAv: -20,
      outPk: -3,
      outAv: -14,
      swr: 1.15,
      psEnabled: true,
      psCorrecting: true,
      psFeedbackLevel: 150,
      psCalState: 0,
      psCalibrationStalled: false,
    });

    const { container, unmount } = render(createElement(TxFidelityAdvisor));

    expect(container.textContent).toContain('Broadcast sweet spot');
    expect(container.textContent).toContain('FIDELITY');
    expect(container.textContent).toContain('NEXT Hold levels; PureSignal is correcting the PA');
    expect(container.textContent).toContain('OUT -3.0 dBFS');
    expect(container.textContent).toMatch(/DENS \d+\/55/);
    expect(container.textContent).toContain('COMP 10.0 dB');
    expect(container.textContent).toContain('SWR 1.15');
    expect(container.textContent).toContain('PSFB 150');

    act(() => {
      useTxStore.setState({ wdspMicPk: -1, alcGr: 12 });
    });

    expect(container.textContent).toContain('Too hot');
    expect(container.textContent).toContain('NEXT Lower mic gain until peaks stay below -6 dBFS');
    unmount();
  });

  it('renders live density against an explicit station-profile target', () => {
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -10,
      micAv: -21,
      alcGr: 3,
      lvlrGr: 4,
      cfcGr: 2,
      compPk: -10,
      compAv: -20,
      outPk: -3,
      outAv: -14,
      swr: 1.15,
    });

    const { container, unmount } = render(
      createElement(TxFidelityAdvisor, { targetSpectralDensity: 100 }),
    );

    expect(container.textContent).toMatch(/DENS \d+\/100/);
    expect(container.textContent).toContain('Under-driven');
    expect(container.textContent).toContain('TX density is below profile target');
    expect(container.textContent).toContain('NEXT Increase mic gain or profile density before adding drive');

    unmount();
  });
});
