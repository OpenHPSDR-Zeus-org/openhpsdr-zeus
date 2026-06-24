/** @vitest-environment jsdom */

// RX2 source-of-truth inversion: the `receivers[]` array is canonical for
// every receiver including RX2 (index 1). Reads prefer `receivers[1]` and fall
// back to the flat *B fields only when the array entry is absent; the optimistic
// writers keep BOTH in sync during the migration off the flat dupes.

import { beforeEach, describe, expect, it } from 'vitest';
import { useConnectionStore } from './connection-store';
import type { ReceiverDto } from '../api/client';
import {
  getReceiverAfGainDb,
  getReceiverFilterHighHz,
  getReceiverFilterLowHz,
  getReceiverFilterPresetName,
  getReceiverMode,
  getReceiverVfoHz,
  optimisticSetReceiverFilter,
  optimisticSetReceiverMode,
  optimisticSetReceiverPreset,
  optimisticSetReceiverVfo,
} from './receiver-state';

function rx(index: number, patch: Partial<ReceiverDto> = {}): ReceiverDto {
  return {
    index,
    enabled: true,
    adcSource: 0,
    vfoHz: 14_000_000,
    mode: 'USB',
    filterLowHz: 100,
    filterHighHz: 2800,
    filterPresetName: 'VAR1',
    afGainDb: 0,
    sampleRateHz: 192_000,
    muted: false,
    ...patch,
  };
}

describe('receiver-state RX2 inversion', () => {
  beforeEach(() => {
    useConnectionStore.setState({
      // Flat *B fields hold STALE values so we can prove reads come from the
      // array, not these.
      vfoBHz: 7_000_000,
      modeB: 'LSB',
      filterLowHzB: -2850,
      filterHighHzB: -100,
      filterPresetNameB: 'VAR1',
      rx2AfGainDb: -12,
      receivers: [
        rx(0, { vfoHz: 14_074_000, mode: 'DIGU' }),
        rx(1, {
          vfoHz: 21_074_000,
          mode: 'CWU',
          filterLowHz: 300,
          filterHighHz: 700,
          filterPresetName: 'VAR2',
          afGainDb: -3,
        }),
      ],
    });
  });

  it('reads RX2 from receivers[1], not the flat *B fields', () => {
    const s = useConnectionStore.getState();
    expect(getReceiverVfoHz(s, 1)).toBe(21_074_000);
    expect(getReceiverMode(s, 1)).toBe('CWU');
    expect(getReceiverFilterLowHz(s, 1)).toBe(300);
    expect(getReceiverFilterHighHz(s, 1)).toBe(700);
    expect(getReceiverFilterPresetName(s, 1)).toBe('VAR2');
    expect(getReceiverAfGainDb(s, 1)).toBe(-3);
    // 'B' is the legacy alias for index 1 — same routing.
    expect(getReceiverVfoHz(s, 'B')).toBe(21_074_000);
  });

  it('falls back to the flat *B fields when receivers[1] is absent', () => {
    useConnectionStore.setState({ receivers: [rx(0, { vfoHz: 14_074_000 })] });
    const s = useConnectionStore.getState();
    expect(getReceiverVfoHz(s, 1)).toBe(7_000_000);
    expect(getReceiverMode(s, 1)).toBe('LSB');
    expect(getReceiverFilterLowHz(s, 1)).toBe(-2850);
    expect(getReceiverAfGainDb(s, 1)).toBe(-12);
  });

  it('optimistic RX2 writes update BOTH receivers[1] and the flat *B mirror', () => {
    optimisticSetReceiverVfo(1, 28_074_000);
    optimisticSetReceiverMode(1, 'DIGL');
    optimisticSetReceiverFilter(1, 250, 3050);
    optimisticSetReceiverPreset(1, 'F5');

    const s = useConnectionStore.getState();
    const entry = s.receivers.find((r) => r.index === 1)!;
    expect(entry.vfoHz).toBe(28_074_000);
    expect(entry.mode).toBe('DIGL');
    expect(entry.filterLowHz).toBe(250);
    expect(entry.filterHighHz).toBe(3050);
    expect(entry.filterPresetName).toBe('F5');

    // Flat mirror stayed in lock-step for the not-yet-migrated direct readers.
    expect(s.vfoBHz).toBe(28_074_000);
    expect(s.modeB).toBe('DIGL');
    expect(s.filterLowHzB).toBe(250);
    expect(s.filterHighHzB).toBe(3050);
    expect(s.filterPresetNameB).toBe('F5');
  });

  it('keeps RX1 (index 0) on the flat primary fields', () => {
    optimisticSetReceiverVfo(0, 18_100_000);
    const s = useConnectionStore.getState();
    expect(s.vfoHz).toBe(18_100_000);
    expect(getReceiverVfoHz(s, 0)).toBe(18_100_000);
  });
});
