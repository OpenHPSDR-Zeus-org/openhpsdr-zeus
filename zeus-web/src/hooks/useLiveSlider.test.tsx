// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for useLiveSlider — the shared "stream during drag, commit
// on release" helper used by every slider component (issue zeus-5k0).
//
// What we lock down here:
//   * push() coalesces multiple values per animation frame so we POST at
//     most once per paint (no endpoint flooding from a wild drag).
//   * flush() dispatches the latest pending value immediately and is a
//     no-op when nothing is pending (safe to wire to pointerUp blindly).
//   * Successive dispatches abort the previous request's AbortSignal so
//     stale acknowledgements don't yank the slider back.
//   * Cleanup on unmount cancels pending rAF and aborts in-flight.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useLiveSlider, type LiveSliderSender } from './useLiveSlider';

// rAF in jsdom is unreliable; the hook falls back to setTimeout(0) when
// requestAnimationFrame isn't a function. We force that path so vi.runAllTimers()
// gives us deterministic flush points.
function stubRafAsTimers() {
  // Make typeof window.requestAnimationFrame !== 'function' to trigger the
  // setTimeout fallback. We can't delete it on the global, so reassign.
  (window as unknown as { requestAnimationFrame?: unknown }).requestAnimationFrame = undefined;
  (window as unknown as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = undefined;
}

interface Harness<T> {
  push: (v: T) => void;
  flush: () => void;
  unmount: () => void;
}

function mount<T>(send: LiveSliderSender<T>): Harness<T> {
  const captured: { push: (v: T) => void; flush: () => void } = {
    push: () => {},
    flush: () => {},
  };
  function Probe() {
    const api = useLiveSlider<T>({ send });
    captured.push = api.push;
    captured.flush = api.flush;
    return null;
  }
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root: Root = createRoot(container);
  act(() => {
    root.render(<Probe />);
  });
  return {
    push: (v) => captured.push(v),
    flush: () => captured.flush(),
    unmount: () => {
      act(() => root.unmount());
      container.remove();
    },
  };
}

describe('useLiveSlider', () => {
  beforeEach(() => {
    stubRafAsTimers();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('coalesces multiple push() calls within one frame into a single dispatch', () => {
    const send = vi.fn().mockResolvedValue(undefined);
    const h = mount<number>(send);
    h.push(1);
    h.push(2);
    h.push(3);
    expect(send).not.toHaveBeenCalled();
    act(() => {
      vi.runAllTimers();
    });
    expect(send).toHaveBeenCalledTimes(1);
    expect(send.mock.calls[0]?.[0]).toBe(3);
    h.unmount();
  });

  it('flush() dispatches the latest pending value immediately', () => {
    const send = vi.fn().mockResolvedValue(undefined);
    const h = mount<number>(send);
    h.push(7);
    h.push(8);
    expect(send).not.toHaveBeenCalled();
    act(() => h.flush());
    expect(send).toHaveBeenCalledTimes(1);
    expect(send.mock.calls[0]?.[0]).toBe(8);
    h.unmount();
  });

  it('flush() is a no-op when nothing is pending', () => {
    const send = vi.fn().mockResolvedValue(undefined);
    const h = mount<number>(send);
    act(() => h.flush());
    expect(send).not.toHaveBeenCalled();
    h.unmount();
  });

  it('does not redispatch the same value back-to-back', () => {
    const send = vi.fn().mockResolvedValue(undefined);
    const h = mount<number>(send);
    h.push(5);
    act(() => vi.runAllTimers());
    h.push(5);
    act(() => vi.runAllTimers());
    expect(send).toHaveBeenCalledTimes(1);
    h.unmount();
  });

  it('aborts the previous in-flight request when a newer value supersedes it', async () => {
    const signals: AbortSignal[] = [];
    const send = vi.fn().mockImplementation((_v: number, signal: AbortSignal) => {
      signals.push(signal);
      return new Promise<void>(() => { /* never resolves */ });
    });
    const h = mount<number>(send);
    h.push(1);
    act(() => vi.runAllTimers());
    h.push(2);
    act(() => vi.runAllTimers());
    expect(signals).toHaveLength(2);
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);
    h.unmount();
  });

  it('aborts in-flight on unmount', () => {
    const signals: AbortSignal[] = [];
    const send = vi.fn().mockImplementation((_v: number, signal: AbortSignal) => {
      signals.push(signal);
      return new Promise<void>(() => {});
    });
    const h = mount<number>(send);
    h.push(42);
    act(() => vi.runAllTimers());
    expect(signals[0]?.aborted).toBe(false);
    h.unmount();
    expect(signals[0]?.aborted).toBe(true);
  });
});
