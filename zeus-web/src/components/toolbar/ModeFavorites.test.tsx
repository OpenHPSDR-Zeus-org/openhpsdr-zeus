/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../meters/__tests__/harness';
import { saveBandMemory, setMode, type RadioStateDto, type ReceiverDto } from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';
import { ModeFavorites } from './ModeFavorites';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    saveBandMemory: vi.fn(() => Promise.resolve()),
    setMode: vi.fn(async () => currentStateDto()),
  };
});

function rxEntry(index: number, patch: Partial<ReceiverDto> = {}): ReceiverDto {
  return {
    index, enabled: true, adcSource: 0, vfoHz: 14_200_000, mode: 'USB',
    filterLowHz: 100, filterHighHz: 2800, filterPresetName: 'VAR1', afGainDb: 0,
    sampleRateHz: 192_000, muted: false, ...patch,
  };
}
function rx2() {
  return useConnectionStore.getState().receivers.find((r) => r.index === 1)!;
}

function resetStores() {
  useConnectionStore.setState({
    status: 'Connected',
    vfoHz: 14_200_000,
    rx2Enabled: true,
    rxFocus: 'B',
    focusedRxIndex: 1,
    selectedRxIndices: [1],
    mode: 'AM',
    receivers: [
      rxEntry(0, { vfoHz: 14_200_000, mode: 'AM' }),
      rxEntry(1, { vfoHz: 7_200_000, mode: 'USB' }),
    ],
  });
  useToolbarFavoritesStore.setState({
    mode: ['USB', 'LSB', 'CWU'],
  });
}

describe('ModeFavorites', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    resetStores();
  });

  it('applies topbar mode selection to focused VFO B', async () => {
    const { container, unmount } = render(createElement(ModeFavorites));
    const lsb = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === 'LSB');

    await act(async () => {
      lsb?.click();
      await Promise.resolve();
    });

    expect(lsb).toBeTruthy();
    expect(setMode).toHaveBeenCalledWith('LSB', undefined, 'B');
    expect(saveBandMemory).toHaveBeenCalledWith('40m', 7_200_000, 'LSB');
    expect(useConnectionStore.getState().mode).toBe('AM');
    expect(rx2().mode).toBe('LSB');

    unmount();
  });
});
