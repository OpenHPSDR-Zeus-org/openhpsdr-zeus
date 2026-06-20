/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  type RadioStateDto,
  type RxMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { BAND_MEMORY_UPDATED_EVENT, type BandMemoryUpdatedDetail } from '../util/band-memory';
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

function dispatchBandMemoryUpdated(
  band: string,
  hz: number,
  mode: RxMode,
) {
  window.dispatchEvent(new CustomEvent<BandMemoryUpdatedDetail>(
    BAND_MEMORY_UPDATED_EVENT,
    { detail: { band, hz, mode } },
  ));
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
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '20m', hz: 14_200_000, mode: 'USB' },
    ]);
    const { container, unmount } = render(createElement(BandButtons));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    act(() => {
      useConnectionStore.setState({ mode: 'LSB' });
      dispatchBandMemoryUpdated('20m', 14_200_000, 'LSB');
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

  it('does not overwrite a remembered band mode from a transient mode snapshot', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '20m', hz: 14_200_000, mode: 'USB' },
    ]);
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
    expect(saveBandMemory).toHaveBeenCalledWith('20m', 14_200_000, 'USB');
    expect(saveBandMemory).not.toHaveBeenCalledWith('20m', 14_200_000, 'LSB');

    unmount();
  });

  it('keeps the destination band memory when the tune response echoes the departing mode', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_150_000, mode: 'LSB' },
      { band: '20m', hz: 14_210_000, mode: 'USB' },
    ]);
    vi.mocked(setVfo).mockImplementation(async (hz) => ({
      ...currentStateDto(),
      vfoHz: hz,
      mode: hz === 14_210_000 ? 'LSB' : currentStateDto().mode,
    }));
    useConnectionStore.setState({ vfoHz: 7_150_000, mode: 'LSB' });

    const { container, unmount } = render(createElement(BandButtons));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    const buttons = Array.from(container.querySelectorAll<HTMLButtonElement>('button'));
    const twentyMeters = buttons.find((button) => button.textContent === '20m');
    const fortyMeters = buttons.find((button) => button.textContent === '40m');

    await act(async () => {
      twentyMeters?.click();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(useConnectionStore.getState().mode).toBe('USB');

    await act(async () => {
      fortyMeters?.click();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(saveBandMemory).toHaveBeenCalledWith('20m', 14_210_000, 'USB');
    expect(saveBandMemory).not.toHaveBeenCalledWith('20m', 14_210_000, 'LSB');

    await act(async () => {
      twentyMeters?.click();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(setMode).toHaveBeenLastCalledWith('USB');

    unmount();
  });
});
