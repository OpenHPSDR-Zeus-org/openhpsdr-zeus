// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const setRadioLoMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, setRadioLo: setRadioLoMock };
});

import { rulerDragTargetHz, useRulerPanGesture } from './use-ruler-pan-gesture';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useVfoLockStore } from '../state/vfo-lock-store';
import * as viewCenter from '../state/view-center';

describe('rulerDragTargetHz', () => {
  it('moves content with the grab direction', () => {
    expect(rulerDragTargetHz(28_000_000, 500, 600, 1000, 100_000)).toBe(27_990_000);
    expect(rulerDragTargetHz(28_000_000, 500, 400, 1000, 100_000)).toBe(28_010_000);
  });

  it('can pan by more than the visible span', () => {
    expect(rulerDragTargetHz(28_000_000, 500, 1700, 1000, 100_000)).toBe(27_880_000);
    expect(rulerDragTargetHz(28_000_000, 500, -700, 1000, 100_000)).toBe(28_120_000);
  });

  it('clamps to the supported radio range', () => {
    expect(rulerDragTargetHz(1000, 0, 100, 100, 10_000)).toBe(0);
    expect(rulerDragTargetHz(59_999_000, 100, 0, 100, 10_000)).toBe(60_000_000);
  });
});

// The standalone ruler-strip gesture (FreqAxis) is a real way to retune RX1 by
// dragging the frequency scale. The VFO lock must freeze it too.
describe('useRulerPanGesture VFO lock', () => {
  let rafCallbacks = new Map<number, FrameRequestCallback>();
  let nextRafHandle = 1;

  function drainRafs(): void {
    let frames = 0;
    while (rafCallbacks.size > 0 && frames < 100) {
      const cbs = Array.from(rafCallbacks.values());
      rafCallbacks.clear();
      for (const cb of cbs) cb(frames * 16.7);
      frames++;
    }
  }

  function pointer(el: HTMLElement, type: string, clientX: number, pointerId = 1): void {
    const ev = new Event(type, { bubbles: true, cancelable: true });
    Object.defineProperties(ev, {
      pointerId: { value: pointerId },
      clientX: { value: clientX },
      clientY: { value: 0 },
      button: { value: 0 },
    });
    el.dispatchEvent(ev);
  }

  beforeEach(() => {
    rafCallbacks = new Map();
    nextRafHandle = 1;
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn((cb: FrameRequestCallback) => {
        const handle = nextRafHandle++;
        rafCallbacks.set(handle, cb);
        return handle;
      }),
    );
    vi.stubGlobal('cancelAnimationFrame', vi.fn((h: number) => rafCallbacks.delete(h)));
    Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLElement.prototype, 'releasePointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLElement.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: vi.fn(() => ({
        left: 0, top: 0, right: 200, bottom: 20, width: 200, height: 20, x: 0, y: 0,
        toJSON: () => ({}),
      })),
    });
    useConnectionStore.setState({ ctunEnabled: false, radioLoHz: 14_200_000, vfoHz: 14_200_000 });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });
    setRadioLoMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
  });

  afterEach(() => {
    useVfoLockStore.setState({ locked: false });
    viewCenter._resetForTest();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('swallows a CTUN-off ruler drag while locked, then resumes when unlocked', async () => {
    const { render, act } = await import('../components/meters/__tests__/harness');
    const { createElement, useRef } = await import('react');

    function Probe() {
      const ref = useRef<HTMLDivElement | null>(null);
      useRulerPanGesture(ref, 'A', true);
      return createElement('div', { ref });
    }

    const { container, unmount } = render(createElement(Probe));
    const el = container.querySelector('div') as HTMLDivElement;

    // Locked → drag is swallowed, radio LO unchanged.
    useVfoLockStore.setState({ locked: true });
    await act(async () => {
      pointer(el, 'pointerdown', 100);
      pointer(el, 'pointermove', 150);
      pointer(el, 'pointerup', 150);
      drainRafs();
      await Promise.resolve();
    });
    expect(setRadioLoMock).not.toHaveBeenCalled();
    expect(useConnectionStore.getState().radioLoHz).toBe(14_200_000);

    // Unlocked → the same drag retunes the radio LO.
    useVfoLockStore.setState({ locked: false });
    await act(async () => {
      pointer(el, 'pointerdown', 100);
      pointer(el, 'pointermove', 150);
      pointer(el, 'pointerup', 150);
      drainRafs();
      await Promise.resolve();
    });
    expect(setRadioLoMock).toHaveBeenCalled();

    unmount();
  });
});
