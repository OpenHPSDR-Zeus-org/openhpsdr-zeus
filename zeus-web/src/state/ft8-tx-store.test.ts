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
    useFt8TxStore.setState({ status: null, txEcho: [] });
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

  describe('TX echo (own transmissions in the decode flow)', () => {
    it('records an echo on a rising transmitting edge with a message', () => {
      useFt8TxStore.getState().ingest(status({ armed: true })); // staged, not yet on air
      expect(useFt8TxStore.getState().txEcho).toHaveLength(0);

      useFt8TxStore.getState().ingest(
        status({ armed: true, transmitting: true, message: 'CQ KB2UKA FN30', slot: 'even', audioHz: 1200 }),
      );
      const echo = useFt8TxStore.getState().txEcho;
      expect(echo).toHaveLength(1);
      expect(echo[0]?.message).toBe('CQ KB2UKA FN30');
      expect(echo[0]?.slot).toBe('even');
      expect(echo[0]?.audioHz).toBe(1200);
      expect(echo[0]?.timeUtcMs).toBeGreaterThan(0);
    });

    it('does not double-record while transmitting stays true', () => {
      const tx = status({ armed: true, transmitting: true, message: 'CQ KB2UKA FN30' });
      useFt8TxStore.getState().ingest(tx);
      useFt8TxStore.getState().ingest({ ...tx }); // another frame, still transmitting
      expect(useFt8TxStore.getState().txEcho).toHaveLength(1);
    });

    it('records a second echo after transmitting falls and rises again', () => {
      useFt8TxStore.getState().ingest(status({ transmitting: true, message: 'CQ KB2UKA FN30' }));
      useFt8TxStore.getState().ingest(status({ armed: true })); // off air between slots
      useFt8TxStore.getState().ingest(status({ transmitting: true, message: 'W1AW KB2UKA -12' }));
      const echo = useFt8TxStore.getState().txEcho;
      expect(echo).toHaveLength(2);
      // Newest first.
      expect(echo[0]?.message).toBe('W1AW KB2UKA -12');
      expect(echo[1]?.message).toBe('CQ KB2UKA FN30');
    });

    it('ignores a transmitting edge with no message', () => {
      useFt8TxStore.getState().ingest(status({ transmitting: true, message: null }));
      expect(useFt8TxStore.getState().txEcho).toHaveLength(0);
    });

    it('clearTxEcho() empties the list', () => {
      useFt8TxStore.getState().ingest(status({ transmitting: true, message: 'CQ KB2UKA FN30' }));
      expect(useFt8TxStore.getState().txEcho).toHaveLength(1);
      useFt8TxStore.getState().clearTxEcho();
      expect(useFt8TxStore.getState().txEcho).toHaveLength(0);
    });
  });
});
