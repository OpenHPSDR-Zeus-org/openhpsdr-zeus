// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Regression guard for fix/filter-drag-double-free (frontend half). Dragging
// the RX filter edge fast used to fire one POST /api/filter per mouse-move; a
// slow request could still be in flight when the next fired, piling concurrent
// POSTs onto the ASP.NET thread pool and racing the (now-serialized) FFTW
// planner. FilterMiniPan now coalesces: at most one /api/filter POST is in
// flight at a time, and the most-recent pending value is sent when it returns.
// These tests prove (a) only one setFilter is outstanding during a fast drag,
// and (b) the terminal POST on pointer-up always carries the final drag value.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { createElement } from 'react';

// Mock setFilter so we control when it resolves. A deferred lets us hold a
// request "in flight" while we fire more pointer-moves. Partial-mock so the
// rest of the client (NR_CONFIG_DEFAULT, etc., pulled in transitively by the
// connection store) keeps its real exports.
const setFilterMock = vi.fn();
vi.mock('../../../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/client')>();
  return {
    ...actual,
    setFilter: (...args: unknown[]) => setFilterMock(...args),
  };
});

import { FilterMiniPan } from '../FilterMiniPan';
import { useConnectionStore } from '../../../state/connection-store';

function deferred<T>(): { promise: Promise<T>; resolve: (v: T) => void } {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

const RIBBON_SPAN_HZ = 12_000;
const RECT = { left: 0, top: 0, width: 600, height: 110, right: 600, bottom: 110, x: 0, y: 0 } as DOMRect;

// Convert a passband-edge Hz value to the clientX a pointer would have on a
// 600px-wide ribbon spanning ±6 kHz — mirrors FilterMiniPan's own mapping.
function hzToClientX(hz: number): number {
  return ((hz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * RECT.width;
}

function makePointerEvent(type: string, clientX: number): Event {
  // jsdom lacks PointerEvent; synthesize one carrying the fields the handlers
  // read (pointerId, button, clientX). React listens for these DOM events.
  const ev = new Event(type, { bubbles: true, cancelable: true });
  Object.assign(ev, { pointerId: 1, button: 0, clientX });
  return ev;
}

describe('FilterMiniPan drag coalescing', () => {
  let container: HTMLDivElement;
  let root: Root;
  let canvas: HTMLCanvasElement;

  beforeEach(() => {
    vi.useFakeTimers();
    setFilterMock.mockReset();

    // jsdom canvas has no 2D context and no pointer capture / RAF — stub the
    // bits FilterMiniPan's effect and handlers touch so the component mounts.
    HTMLCanvasElement.prototype.getContext = vi.fn(() => ({
      clearRect: vi.fn(), fillRect: vi.fn(), beginPath: vi.fn(), moveTo: vi.fn(),
      lineTo: vi.fn(), stroke: vi.fn(), fill: vi.fn(), save: vi.fn(),
      restore: vi.fn(), fillText: vi.fn(), setLineDash: vi.fn(), arc: vi.fn(),
      closePath: vi.fn(), rect: vi.fn(), clip: vi.fn(), translate: vi.fn(),
      scale: vi.fn(), measureText: vi.fn(() => ({ width: 0 })),
    })) as unknown as typeof HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.setPointerCapture = vi.fn();
    HTMLCanvasElement.prototype.releasePointerCapture = vi.fn();
    HTMLCanvasElement.prototype.hasPointerCapture = vi.fn(() => true);
    HTMLCanvasElement.prototype.getBoundingClientRect = () => RECT;
    vi.stubGlobal('requestAnimationFrame', () => 0);
    vi.stubGlobal('cancelAnimationFrame', () => {});
    vi.stubGlobal('ResizeObserver', class {
      observe() {} unobserve() {} disconnect() {}
    });
    vi.stubGlobal('performance', { now: () => Date.now() });

    useConnectionStore.setState({
      filterLowHz: 150,
      filterHighHz: 2850,
      filterPresetName: 'VAR1',
    });

    container = document.createElement('div');
    document.body.appendChild(container);
    act(() => {
      root = createRoot(container);
      root.render(createElement(FilterMiniPan));
    });
    canvas = container.querySelector('canvas')!;
  });

  afterEach(() => {
    act(() => { root.unmount(); });
    container.remove();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('keeps at most one setFilter POST in flight during a fast drag', async () => {
    const d1 = deferred<unknown>();
    setFilterMock.mockReturnValueOnce(d1.promise);
    // Every subsequent (coalesced re-flush) call returns a resolved promise so
    // the component's .catch()/.finally() chain has something to attach to.
    setFilterMock.mockReturnValue(Promise.resolve(useConnectionStore.getState()));

    // Grab the HIGH edge (2850 Hz) and drag it fast — many moves inside the
    // 50ms throttle window while the first POST is unresolved.
    act(() => {
      canvas.dispatchEvent(makePointerEvent('pointerdown', hzToClientX(2850)));
    });

    // First move triggers the immediate flush (elapsed >= interval since
    // lastWriteAt started at 0) → exactly one setFilter in flight.
    act(() => {
      canvas.dispatchEvent(makePointerEvent('pointermove', hzToClientX(2700)));
    });
    expect(setFilterMock).toHaveBeenCalledTimes(1);

    // Flood more moves while d1 is still pending and advance fake time past the
    // throttle interval each time. The coalescing guard must NOT start a second
    // POST until d1 resolves — they collapse into a single pending "dirty".
    for (let hz = 2650; hz >= 2300; hz -= 50) {
      act(() => {
        vi.advanceTimersByTime(60);
        canvas.dispatchEvent(makePointerEvent('pointermove', hzToClientX(hz)));
      });
    }
    // Let any queued throttle timers fire — still only the one in-flight POST.
    act(() => { vi.advanceTimersByTime(200); });
    expect(setFilterMock).toHaveBeenCalledTimes(1);

    // Resolve the in-flight POST: the coalesced "dirty" value flushes as the
    // second call, carrying the most-recent drag position.
    await act(async () => {
      d1.resolve(undefined);
      await Promise.resolve();
    });
    expect(setFilterMock).toHaveBeenCalledTimes(2);
    // Latest dragged HIGH edge was 2300 Hz → second POST sends (150, 2300).
    expect(setFilterMock.mock.calls[1]![0]).toBe(150);
    expect(setFilterMock.mock.calls[1]![1]).toBe(2300);
  });

  it('sends the final drag value as the terminal POST on pointer-up', async () => {
    const d1 = deferred<unknown>();
    setFilterMock.mockReturnValueOnce(d1.promise);
    setFilterMock.mockReturnValue(Promise.resolve(useConnectionStore.getState()));

    act(() => {
      canvas.dispatchEvent(makePointerEvent('pointerdown', hzToClientX(2850)));
      canvas.dispatchEvent(makePointerEvent('pointermove', hzToClientX(2700)));
    });
    expect(setFilterMock).toHaveBeenCalledTimes(1);

    // More fast moves while in flight — coalesced, not sent yet.
    for (let hz = 2600; hz >= 2400; hz -= 100) {
      act(() => {
        vi.advanceTimersByTime(60);
        canvas.dispatchEvent(makePointerEvent('pointermove', hzToClientX(hz)));
      });
    }

    // Resolve the in-flight POST, then move once more and release.
    await act(async () => { d1.resolve(undefined); await Promise.resolve(); });
    act(() => {
      vi.advanceTimersByTime(60);
      canvas.dispatchEvent(makePointerEvent('pointermove', hzToClientX(2200)));
    });
    await act(async () => {
      canvas.dispatchEvent(makePointerEvent('pointerup', hzToClientX(2200)));
      await Promise.resolve();
    });

    // The LAST setFilter invocation must carry the final drag position (2200),
    // never a stale intermediate value.
    const last = setFilterMock.mock.calls[setFilterMock.mock.calls.length - 1]!;
    expect(last[0]).toBe(150);
    expect(last[1]).toBe(2200);
  });
});
