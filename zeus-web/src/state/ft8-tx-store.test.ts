// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it } from 'vitest';
import { useFt8TxStore, type Ft8TxStatus } from './ft8-tx-store';

function status(over: Partial<Ft8TxStatus> = {}): Ft8TxStatus {
  return {
    armed: false,
    transmitting: false,
    mode: 'FT8',
    message: null,
    audioHz: 1500,
    slot: '',
    watchdogSecsRemaining: 0,
    lastTxSlotMs: null,
    nativeAvailable: true,
    ...over,
  };
}

describe('ft8-tx-store (0x3A status frames)', () => {
  beforeEach(() => {
    useFt8TxStore.setState({ status: null });
  });

  it('starts with no status', () => {
    expect(useFt8TxStore.getState().status).toBeNull();
  });

  it('ingest() replaces the status snapshot', () => {
    useFt8TxStore.getState().ingest(status({ armed: true, mode: 'FT8' }));
    expect(useFt8TxStore.getState().status?.armed).toBe(true);

    useFt8TxStore
      .getState()
      .ingest(status({ armed: true, transmitting: true, message: 'CQ KB2UKA FN12', slot: 'even' }));
    const s = useFt8TxStore.getState().status;
    expect(s?.transmitting).toBe(true);
    expect(s?.message).toBe('CQ KB2UKA FN12');
    expect(s?.slot).toBe('even');
  });

  it('carries a WSPR status through unchanged (shared frame)', () => {
    useFt8TxStore.getState().ingest(status({ mode: 'WSPR', armed: true, watchdogSecsRemaining: 1800 }));
    const s = useFt8TxStore.getState().status;
    expect(s?.mode).toBe('WSPR');
    expect(s?.watchdogSecsRemaining).toBe(1800);
  });
});
