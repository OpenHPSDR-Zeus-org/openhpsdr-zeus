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
  type RxMode,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';
import { useToolbarFavoritesStore } from '../../state/toolbar-favorites-store';
import { BAND_MEMORY_UPDATED_EVENT, type BandMemoryUpdatedDetail } from '../../util/band-memory';
import { BandFavorites } from './BandFavorites';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

// RX2 now lives in receivers[1]. Helpers to seed/mutate/read it in tests.
function rxEntry(index: number, patch: Record<string, unknown> = {}) {
  return {
    index,
    enabled: true,
    adcSource: 0,
    vfoHz: 14_200_000,
    mode: 'USB' as RxMode,
    filterLowHz: 100,
    filterHighHz: 2800,
    filterPresetName: 'VAR1',
    afGainDb: 0,
    sampleRateHz: 192_000,
    muted: false,
    ...patch,
  };
}
function setRx2(patch: Record<string, unknown>) {
  useConnectionStore.setState((s) => ({
    receivers: s.receivers.map((r) => (r.index === 1 ? { ...r, ...patch } : r)),
  }));
}
function rx2() {
  return useConnectionStore.getState().receivers.find((r) => r.index === 1)!;
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
    rx2Enabled: true,
    rxFocus: 'B',
    focusedRxIndex: 1,
    selectedRxIndices: [1],
    mode: 'USB',
    receivers: [
      rxEntry(0, { vfoHz: 14_200_000, mode: 'USB' }),
      rxEntry(1, {
        vfoHz: 7_200_000,
        mode: 'LSB',
        filterLowHz: -2850,
        filterHighHz: -150,
        filterPresetName: 'VAR1',
      }),
    ],
  });
  useToolbarFavoritesStore.setState({
    band: ['40m', '20m', '15m'],
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
    expect(setVfoB).toHaveBeenCalledWith(7_074_000, undefined);
    expect(setVfo).not.toHaveBeenCalled();
    expect(setMode).not.toHaveBeenCalled();
    expect(rx2().vfoHz).toBe(7_074_000);
    expect(useConnectionStore.getState().vfoHz).toBe(14_200_000);

    unmount();
  });

  it('flushes focused receiver mode memory before changing bands', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_200_000, mode: 'LSB' },
    ]);
    const { container, unmount } = render(createElement(BandFavorites));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    act(() => {
      setRx2({ mode: 'CWU' });
      dispatchBandMemoryUpdated('40m', 7_200_000, 'CWU');
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
    expect(setVfoB).toHaveBeenCalledWith(14_074_000, undefined);
    expect(setVfo).not.toHaveBeenCalled();
    expect(useConnectionStore.getState().mode).toBe('USB');
    expect(rx2().mode).toBe('DIGU');

    unmount();
  });

  it('does not overwrite focused-receiver band mode from a transient mode snapshot', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_200_000, mode: 'LSB' },
    ]);
    const { container, unmount } = render(createElement(BandFavorites));
    await act(async () => {
      await Promise.resolve();
    });
    vi.clearAllMocks();

    act(() => {
      setRx2({ mode: 'CWU' });
    });
    const twentyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '20m');

    await act(async () => {
      twentyMeters?.click();
      await Promise.resolve();
    });

    expect(twentyMeters).toBeTruthy();
    expect(saveBandMemory).toHaveBeenCalledWith('40m', 7_200_000, 'LSB');
    expect(saveBandMemory).not.toHaveBeenCalledWith('40m', 7_200_000, 'CWU');

    unmount();
  });

  it('keeps focused-receiver band memory when the tune response echoes the departing mode', async () => {
    vi.useFakeTimers();
    vi.mocked(fetchBandMemory).mockResolvedValue([
      { band: '40m', hz: 7_150_000, mode: 'LSB' },
      { band: '20m', hz: 14_210_000, mode: 'USB' },
    ]);
    vi.mocked(setVfoB).mockImplementation(async (hz) => {
      const s = currentStateDto();
      return {
        ...s,
        receivers: s.receivers?.map((r) =>
          r.index === 1
            ? { ...r, vfoHz: hz, mode: hz === 14_210_000 ? 'LSB' : r.mode }
            : r,
        ),
      };
    });
    setRx2({ vfoHz: 7_150_000, mode: 'LSB' });

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

    expect(rx2().mode).toBe('USB');

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

  it('gangs a band selection across every selected receiver', async () => {
    // RX1 + RX2 both selected, RX1 focused. A band click must retune BOTH.
    useConnectionStore.setState({
      vfoHz: 14_200_000,
      vfoBHz: 14_200_000,
      mode: 'USB',
      modeB: 'USB',
      rx2Enabled: true,
      focusedRxIndex: 0,
      rxFocus: 'A',
      selectedRxIndices: [0, 1],
    });

    const { container, unmount } = render(createElement(BandFavorites));
    const fortyMeters = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((button) => button.textContent === '40m');

    await act(async () => {
      fortyMeters?.click();
      await Promise.resolve();
    });

    // Both receivers were commanded to the 40m centre (the ganged contract).
    // The focused receiver (RX1) reconciles the store from its own response;
    // RX2's committed value lands on the next state poll, so we assert the
    // posts rather than the transiently-reconciled sibling field.
    expect(setVfo).toHaveBeenCalledWith(7_074_000, undefined);
    expect(setVfoB).toHaveBeenCalledWith(7_074_000, undefined);
    expect(useConnectionStore.getState().vfoHz).toBe(7_074_000);

    unmount();
  });
});
