/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../meters/__tests__/harness';
import { setMode, setVfo, setVfoB, type RadioStateDto } from '../../api/client';
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
  });
  useToolbarFavoritesStore.setState({
    band: ['40m', '20m', '15m'],
  });
}

describe('BandFavorites', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    resetStores();
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
});
