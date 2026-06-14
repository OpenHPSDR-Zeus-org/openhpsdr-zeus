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
      lvlrGr: 0,
      cfcGr: 0,
      alcGr: 0,
      outPk: -Infinity,
      psEnabled: false,
      psCorrecting: false,
    });
  });

  it('renders and reacts to TX meter updates without re-entering render', () => {
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -10,
      alcGr: 3,
      lvlrGr: 4,
      cfcGr: 2,
      outPk: -3,
      psEnabled: true,
      psCorrecting: true,
    });

    const { container, unmount } = render(createElement(TxFidelityAdvisor));

    expect(container.textContent).toContain('Broadcast sweet spot');
    expect(container.textContent).toContain('FIDELITY');

    act(() => {
      useTxStore.setState({ wdspMicPk: -1, alcGr: 12 });
    });

    expect(container.textContent).toContain('Too hot');
    unmount();
  });
});
