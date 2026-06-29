// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
// localStorage polyfill + IS_REACT_ACT_ENVIRONMENT side-effects (must come
// before any store import that pulls in zustand/persist).
import '../components/meters/__tests__/harness';
import { act, renderHook } from '../components/meters/__tests__/harness';
import { useLayoutStore } from '../state/layout-store';
import { useTxStore } from '../state/tx-store';
import {
  EMPTY_WORKSPACE_LAYOUT,
  type WorkspaceLayout,
} from '../layout/workspace';
import { useTxLayoutAutoSwitch } from './useTxLayoutAutoSwitch';

const txWs: WorkspaceLayout = {
  schemaVersion: 8,
  tiles: [],
  autoSwitchOnTx: true,
};
const rxWs = EMPTY_WORKSPACE_LAYOUT;

function seedRadioWithTxLayout() {
  useLayoutStore.setState({
    radioKey: 'radio-1',
    layouts: [
      { id: 'rx', name: 'RX', layoutJson: JSON.stringify(rxWs) },
      { id: 'tx', name: 'TX', layoutJson: JSON.stringify(txWs) },
    ],
    activeLayoutId: 'rx',
    workspace: rxWs,
    isLoaded: true,
  });
  useTxStore.setState({ moxOn: false, tunOn: false });
}

describe('useTxLayoutAutoSwitch (issue #1164)', () => {
  beforeEach(() => {
    (globalThis as unknown as { fetch: typeof fetch }).fetch = vi
      .fn()
      .mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
  });

  it('switches to the TX layout on MOX and back to the prior layout on un-key', () => {
    seedRadioWithTxLayout();
    const view = renderHook(() => useTxLayoutAutoSwitch());

    act(() => {
      useTxStore.setState({ moxOn: true });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('tx');

    act(() => {
      useTxStore.setState({ moxOn: false });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('rx');

    view.unmount();
  });

  it('also fires on TUN (TUN counts as transmit)', () => {
    seedRadioWithTxLayout();
    const view = renderHook(() => useTxLayoutAutoSwitch());

    act(() => {
      useTxStore.setState({ tunOn: true });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('tx');

    act(() => {
      useTxStore.setState({ tunOn: false });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('rx');

    view.unmount();
  });

  it('is a no-op when no layout is marked autoSwitchOnTx', () => {
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        { id: 'rx', name: 'RX', layoutJson: JSON.stringify(rxWs) },
        { id: 'other', name: 'Other', layoutJson: JSON.stringify(rxWs) },
      ],
      activeLayoutId: 'rx',
      workspace: rxWs,
      isLoaded: true,
    });
    useTxStore.setState({ moxOn: false, tunOn: false });

    const view = renderHook(() => useTxLayoutAutoSwitch());
    act(() => {
      useTxStore.setState({ moxOn: true });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('rx');
    act(() => {
      useTxStore.setState({ moxOn: false });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('rx');
    view.unmount();
  });

  it('does not yank the operator back if they manually switched layouts mid-TX', () => {
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        { id: 'rx', name: 'RX', layoutJson: JSON.stringify(rxWs) },
        { id: 'contest', name: 'Contest', layoutJson: JSON.stringify(rxWs) },
        { id: 'tx', name: 'TX', layoutJson: JSON.stringify(txWs) },
      ],
      activeLayoutId: 'rx',
      workspace: rxWs,
      isLoaded: true,
    });
    useTxStore.setState({ moxOn: false, tunOn: false });

    const view = renderHook(() => useTxLayoutAutoSwitch());

    act(() => {
      useTxStore.setState({ moxOn: true });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('tx');

    // Operator clicks "Contest" mid-TX — restore is cancelled.
    act(() => {
      useLayoutStore.getState().setActiveLayout('contest');
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('contest');

    act(() => {
      useTxStore.setState({ moxOn: false });
    });
    expect(useLayoutStore.getState().activeLayoutId).toBe('contest');

    view.unmount();
  });
});
