/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { saveBandMemory, setMode, type RadioStateDto } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { act, render } from './meters/__tests__/harness';
import { ModeBandwidth } from './ModeBandwidth';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    saveBandMemory: vi.fn(() => Promise.resolve()),
    setMode: vi.fn(async () => currentStateDto()),
  };
});

function resetStore() {
  useConnectionStore.setState({
    status: 'Connected',
    vfoHz: 14_200_000,
    rx2Enabled: false,
    rxFocus: 'A',
    mode: 'USB',
  });
}

describe('ModeBandwidth', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    resetStore();
  });

  it('persists the selected mode in current band memory', async () => {
    const { container, unmount } = render(createElement(ModeBandwidth));
    const am = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === 'AM');

    await act(async () => {
      am?.click();
      await Promise.resolve();
    });

    expect(am).toBeTruthy();
    expect(setMode).toHaveBeenCalledWith('AM', undefined, 'A');
    expect(saveBandMemory).toHaveBeenCalledWith('20m', 14_200_000, 'AM');
    expect(useConnectionStore.getState().mode).toBe('AM');

    unmount();
  });
});
