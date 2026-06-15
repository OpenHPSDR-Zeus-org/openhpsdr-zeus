// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode, createElement } from 'react';

import { act, render } from './meters/__tests__/harness';
import { Waterfall } from './Waterfall';

const releaseFrameConsumerMock = vi.hoisted(() => vi.fn());
const createWfRendererMock = vi.hoisted(() => vi.fn());

vi.mock('../gl/waterfall', () => ({
  createWfRenderer: createWfRendererMock,
}));

vi.mock('../state/display-store', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../state/display-store')>();
  return {
    ...actual,
    registerFrameConsumer: vi.fn(() => releaseFrameConsumerMock),
  };
});

vi.mock('./FilterCursorOverlay', () => ({
  FilterCursorOverlay: () => null,
}));

vi.mock('./NotchOverlay', () => ({
  NotchOverlay: () => null,
}));

vi.mock('./PassbandOverlay', () => ({
  PassbandOverlay: () => null,
}));

vi.mock('./WfDbScale', () => ({
  WfDbScale: () => null,
}));

vi.mock('../util/use-pan-tune-gesture', () => ({
  usePanTuneGesture: () => undefined,
}));

describe('Waterfall', () => {
  beforeEach(() => {
    createWfRendererMock.mockImplementation(() => {
      throw new Error('shader compile failed (vertex): no compiler log');
    });
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({})),
    });
    releaseFrameConsumerMock.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('degrades instead of crashing when the WebGL renderer fails to build', () => {
    const { container, unmount } = render(createElement(Waterfall));

    expect(container.textContent).toContain('Waterfall renderer unavailable');

    unmount();

    expect(releaseFrameConsumerMock).toHaveBeenCalledTimes(1);
  });

  it('does not lose the WebGL context during StrictMode effect remount', () => {
    vi.useFakeTimers();
    const loseContext = vi.fn();
    const renderer = {
      caps: { floatLinear: true, colorBufferFloat: true, gpu: 'test-gpu' },
      resize: vi.fn(),
      pushFrame: vi.fn(),
      draw: vi.fn(),
      setColormap: vi.fn(),
      setPopMode: vi.fn(),
      setTransparent: vi.fn(),
      debugState: vi.fn(() => ({
        texWidth: 0,
        writeRow: 0,
        lastViewOffsetUv: 0,
        contextLost: false,
      })),
      clearHistory: vi.fn(),
      dispose: vi.fn(),
    };
    createWfRendererMock.mockReturnValue(renderer);

    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({
        getExtension: vi.fn((name: string) =>
          name === 'WEBGL_lose_context' ? { loseContext } : null,
        ),
      })),
    });
    vi.stubGlobal(
      'ResizeObserver',
      class ResizeObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal(
      'IntersectionObserver',
      class IntersectionObserver {
        observe = vi.fn();
        disconnect = vi.fn();
      },
    );
    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1));
    vi.stubGlobal('cancelAnimationFrame', vi.fn());

    const { unmount } = render(
      createElement(StrictMode, null, createElement(Waterfall)),
    );

    act(() => {
      vi.advanceTimersByTime(250);
    });

    expect(loseContext).not.toHaveBeenCalled();

    unmount();

    act(() => {
      vi.advanceTimersByTime(250);
    });

    expect(loseContext).toHaveBeenCalledTimes(1);
  });
});
