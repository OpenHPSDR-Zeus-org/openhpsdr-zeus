// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

/** @vitest-environment jsdom */

import { describe, expect, it, beforeEach, beforeAll } from 'vitest';
import { renderHook, act } from './harness';
import { MeterReadingId } from '../meterCatalog';
import { useMeterReading } from '../useMeterReading';
import { useRxMetersStore } from '../../../state/rx-meters-store';
import { useTxStore } from '../../../state/tx-store';

// Vitest's `jsdom` environment exposes `window.localStorage` as a getter that
// can return a stripped storage stub when it's been wired up without a
// backing file (see the "--localstorage-file was provided without a valid
// path" warning the runner emits). The tx-store uses Zustand's persist
// middleware, which calls setItem on every setState — without a real
// Storage we need to install a tiny in-memory polyfill before any store
// mutation runs.
function ensureLocalStorage() {
  if (typeof globalThis === 'undefined') return;
  const g = globalThis as unknown as { localStorage?: Storage };
  if (g.localStorage && typeof g.localStorage.setItem === 'function') return;
  const store = new Map<string, string>();
  const polyfill: Storage = {
    get length() {
      return store.size;
    },
    clear() {
      store.clear();
    },
    getItem(key) {
      return store.has(key) ? (store.get(key) as string) : null;
    },
    key(index) {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key) {
      store.delete(key);
    },
    setItem(key, value) {
      store.set(key, String(value));
    },
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: polyfill,
  });
}

function resetStores() {
  useRxMetersStore.setState({
    signalPk: -Infinity,
    signalAv: -Infinity,
    adcPk: -Infinity,
    adcAv: -Infinity,
    agcGain: 0,
    agcEnvPk: -Infinity,
    agcEnvAv: -Infinity,
  });
  useTxStore.setState({ fwdWatts: 0, refWatts: 0, swr: 1 });
}

describe('useMeterReading', () => {
  beforeAll(ensureLocalStorage);
  beforeEach(resetStores);

  it('returns the rx-meters-store signal pk', () => {
    const { result } = renderHook(() => useMeterReading(MeterReadingId.RxSignalPk));
    expect(result.current).toBe(-Infinity);
    act(() => {
      useRxMetersStore.setState({ signalPk: -73 });
    });
    expect(result.current).toBe(-73);
  });

  it('returns the tx-store fwd watts', () => {
    const { result } = renderHook(() => useMeterReading(MeterReadingId.TxFwdWatts));
    expect(result.current).toBe(0);
    act(() => {
      useTxStore.setState({ fwdWatts: 4.2 });
    });
    expect(result.current).toBeCloseTo(4.2);
  });

  it('returns the tx-store SWR', () => {
    const { result } = renderHook(() => useMeterReading(MeterReadingId.TxSwr));
    expect(result.current).toBe(1);
    act(() => {
      useTxStore.setState({ swr: 1.7 });
    });
    expect(result.current).toBeCloseTo(1.7);
  });

  it('returns -Infinity (the "no frame yet" sentinel) for adc pk before any frame', () => {
    const { result } = renderHook(() => useMeterReading(MeterReadingId.RxAdcPk));
    expect(Number.isFinite(result.current)).toBe(false);
  });

  it('updates when the underlying RX store changes', () => {
    const { result, rerender } = renderHook(() =>
      useMeterReading(MeterReadingId.RxAgcGain),
    );
    expect(result.current).toBe(0);
    act(() => {
      useRxMetersStore.setState({ agcGain: -12 });
    });
    rerender();
    expect(result.current).toBe(-12);
  });
});
