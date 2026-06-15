// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { render } from './meters/__tests__/harness';
import { Waterfall } from './Waterfall';

const releaseFrameConsumerMock = vi.hoisted(() => vi.fn());

vi.mock('../gl/waterfall', () => ({
  createWfRenderer: vi.fn(() => {
    throw new Error('shader compile failed (vertex): no compiler log');
  }),
}));

vi.mock('../state/display-store', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../state/display-store')>();
  return {
    ...actual,
    registerFrameConsumer: vi.fn(() => releaseFrameConsumerMock),
  };
});

vi.mock('./WfDbScale', () => ({
  WfDbScale: () => null,
}));

vi.mock('../util/use-pan-tune-gesture', () => ({
  usePanTuneGesture: () => undefined,
}));

describe('Waterfall', () => {
  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: vi.fn(() => ({})),
    });
    releaseFrameConsumerMock.mockClear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('degrades instead of crashing when the WebGL renderer fails to build', () => {
    const { container, unmount } = render(createElement(Waterfall));

    expect(container.textContent).toContain('Waterfall renderer unavailable');

    unmount();

    expect(releaseFrameConsumerMock).toHaveBeenCalledTimes(1);
  });
});
