/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../meters/__tests__/harness';
import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  setVfoB,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';
import { BandFavorites } from './BandFavorites';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    fetchBandMemory: vi.fn(() => Promise.resolve([])),
    saveBandMemory: vi.fn(() => Promise.resolve()),
    setMode: vi.fn(async () => currentStateDto()),
    setVfo: vi.fn(async () => currentStateDto()),
    setVfoB: vi.fn(async () => currentStateDto()),
  };
});

function resetStores() {
  useConnectionStore.setState({
    status: 'Connected',
    vfoHz: 14_200_000,
    vfoBHz: 7_200_000,
    rx2Enabled: true,
    rxFocus: 'B',
    mode: 'USB',
    modeB: 'LSB',
    filterLowHzB: -2850,
    filterHighHzB: -150,
    filterPresetNameB: 'VAR1',
  });
  useToolbarFavoritesStore.setState({
    band: ['40m', '20m', '15m'],
  });
}

describe('BandFavorites', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(fetchBandMemory).mockResolvedValue([]);
    resetStores();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('applies topbar band selection to focused VFO B', async () => {
    const { container, unmount } = render(createElement(BandFavorites));
    const fortyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '40m');

    await act(async () => {
      fortyMeters?.click();
      await Promise.resolve();
    });

    expect(fortyMeters).toBeTruthy();
    expect(setVfoB).toHaveBeenCalledWith(7_074_000);
    expect(setVfo).not.toHaveBeenCalled();
    expect(setMode).not.toHaveBeenCalled();
    expect(useConnectionStore.getState().vfoBHz).toBe(7_074_000);
    expect(useConnectionStore.getState().vfoHz).toBe(14_200_000);

    unmount();
  });

  it('flushes focused receiver mode memory before changing bands', async () => {
    vi.useFakeTimers();
    const { container, unmount } = render(createElement(BandFavorites));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    act(() => {
      useConnectionStore.setState({ modeB: 'CWU' });
    });
    const twentyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '20m');

    await act(async () => {
      twentyMeters?.click();
      await Promise.resolve();
    });

    expect(twentyMeters).toBeTruthy();
    expect(saveBandMemory).toHaveBeenCalledWith('40m', 7_200_000, 'CWU');
    expect(saveBandMemory).not.toHaveBeenCalledWith('40m', 7_200_000, 'LSB');

    unmount();
  });

  it('restores the saved focused-receiver mode when selecting a remembered band', async () => {
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '20m', hz: 14_074_000, mode: 'DIGU' },
    ]);
    const { container, unmount } = render(createElement(BandFavorites));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    const twentyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '20m');

    await act(async () => {
      twentyMeters?.click();
      await Promise.resolve();
    });

    expect(twentyMeters).toBeTruthy();
    expect(setMode).toHaveBeenCalledWith('DIGU', undefined, 'B');
    expect(setVfoB).toHaveBeenCalledWith(14_074_000);
    expect(setVfo).not.toHaveBeenCalled();
    expect(useConnectionStore.getState().mode).toBe('USB');
    expect(useConnectionStore.getState().modeB).toBe('DIGU');

    unmount();
  });

  it('keeps focused-receiver band memory when the tune response echoes the departing mode', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_150_000, mode: 'LSB' },
      { band: '20m', hz: 14_210_000, mode: 'USB' },
    ]);
    vi.mocked(setVfoB).mockImplementation(async (hz) => ({
      ...currentStateDto(),
      vfoBHz: hz,
      modeB: hz === 14_210_000 ? 'LSB' : currentStateDto().modeB,
    }));
    useConnectionStore.setState({ vfoBHz: 7_150_000, modeB: 'LSB' });

    const { container, unmount } = render(createElement(BandFavorites));
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

    expect(useConnectionStore.getState().modeB).toBe('USB');

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

    expect(setMode).toHaveBeenLastCalledWith('USB', undefined, 'B');

    unmount();
  });
});
