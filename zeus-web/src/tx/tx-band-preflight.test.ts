/** @vitest-environment jsdom */

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useConnectionStore } from '../state/connection-store';
import {
  resolveTxBandWarning,
  runTxBandPreflight,
  subscribeTxBandWarning,
  type TxBandWarningRequest,
} from './tx-band-preflight';

function setTxSelection(txVfo: 'A' | 'B', vfoHz: number, vfoBHz: number) {
  useConnectionStore.setState({
    txVfo,
    vfoHz,
    vfoBHz,
  });
}

describe('runTxBandPreflight', () => {
  beforeEach(() => {
    resolveTxBandWarning(false);
    localStorage.clear();
    setTxSelection('B', 14_200_000, 7_200_000);
  });

  afterEach(() => {
    resolveTxBandWarning(false);
    localStorage.clear();
  });

  it('asks before first TX on the selected VFO band and records approval', async () => {
    const requests: Array<TxBandWarningRequest | null> = [];
    const unsubscribe = subscribeTxBandWarning((request) => requests.push(request));

    const preflight = runTxBandPreflight();

    expect(requests.at(-1)).toMatchObject({
      txVfo: 'B',
      band: '40m',
      freqHz: 7_200_000,
    });

    resolveTxBandWarning(true);

    await expect(preflight).resolves.toBe(true);
    expect(localStorage.getItem('zeus.tx.lastAcknowledgedBand')).toBe('B:40m');
    unsubscribe();
  });

  it('does not ask again for the same acknowledged TX VFO band', async () => {
    const first = runTxBandPreflight();
    resolveTxBandWarning(true);
    await expect(first).resolves.toBe(true);

    const requests: Array<TxBandWarningRequest | null> = [];
    const unsubscribe = subscribeTxBandWarning((request) => requests.push(request));

    await expect(runTxBandPreflight()).resolves.toBe(true);

    expect(requests).toEqual([null]);
    unsubscribe();
  });

  it('does not acknowledge the band when the user cancels', async () => {
    const preflight = runTxBandPreflight();

    resolveTxBandWarning(false);

    await expect(preflight).resolves.toBe(false);
    expect(localStorage.getItem('zeus.tx.lastAcknowledgedBand')).toBeNull();
  });

  it('honors the do-not-show preference without prompting on a new band', async () => {
    localStorage.setItem('zeus.tx.newBandWarning.dismissed', '1');
    const requests: Array<TxBandWarningRequest | null> = [];
    const unsubscribe = subscribeTxBandWarning((request) => requests.push(request));

    await expect(runTxBandPreflight()).resolves.toBe(true);

    expect(requests).toEqual([null]);
    expect(localStorage.getItem('zeus.tx.lastAcknowledgedBand')).toBe('B:40m');
    unsubscribe();
  });

  it('skips preflight when the selected TX frequency is outside a known band', async () => {
    setTxSelection('B', 14_200_000, 99_000_000);
    const requests: Array<TxBandWarningRequest | null> = [];
    const unsubscribe = subscribeTxBandWarning((request) => requests.push(request));

    await expect(runTxBandPreflight()).resolves.toBe(true);

    expect(requests).toEqual([null]);
    unsubscribe();
  });
});
