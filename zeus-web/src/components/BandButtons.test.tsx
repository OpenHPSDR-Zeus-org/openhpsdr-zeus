/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  type RadioStateDto,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { act, render } from './meters/__tests__/harness';
import { BandButtons } from './BandButtons';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    fetchBandMemory: vi.fn(() => Promise.resolve([])),
    saveBandMemory: vi.fn(() => Promise.resolve()),
    setMode: vi.fn(async () => currentStateDto()),
    setVfo: vi.fn(async () => currentStateDto()),
  };
});

function resetStore() {
  useConnectionStore.setState({
    status: 'Connected',
    vfoHz: 14_200_000,
    mode: 'USB',
    rx2Enabled: false,
    rxFocus: 'A',
  });
}

describe('BandButtons', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(fetchBandMemory).mockResolvedValue([]);
    resetStore();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('flushes mode memory for the departing band before selecting another band', async () => {
    vi.useFakeTimers();
    const { container, unmount } = render(createElement(BandButtons));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    act(() => {
      useConnectionStore.setState({ mode: 'LSB' });
    });
    const fortyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '40m');

    await act(async () => {
      fortyMeters?.click();
      await Promise.resolve();
    });

    expect(fortyMeters).toBeTruthy();
    expect(saveBandMemory).toHaveBeenCalledWith('20m', 14_200_000, 'LSB');
    expect(saveBandMemory).not.toHaveBeenCalledWith('20m', 14_200_000, 'USB');
    expect(setVfo).toHaveBeenCalledWith(7_074_000);

    unmount();
  });

  it('restores the saved mode when selecting a remembered band', async () => {
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_150_000, mode: 'AM' },
    ]);
    const { container, unmount } = render(createElement(BandButtons));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    const fortyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '40m');

    await act(async () => {
      fortyMeters?.click();
      await Promise.resolve();
    });

    expect(fortyMeters).toBeTruthy();
    expect(setVfo).toHaveBeenCalledWith(7_150_000);
    expect(setMode).toHaveBeenCalledWith('AM');
    const setModeCallOrder = vi.mocked(setMode).mock.invocationCallOrder[0];
    const setVfoCallOrder = vi.mocked(setVfo).mock.invocationCallOrder[0];
    expect(setModeCallOrder).toBeDefined();
    expect(setVfoCallOrder).toBeDefined();
    expect(setModeCallOrder!).toBeLessThan(setVfoCallOrder!);
    expect(useConnectionStore.getState().mode).toBe('AM');

    unmount();
  });
});
