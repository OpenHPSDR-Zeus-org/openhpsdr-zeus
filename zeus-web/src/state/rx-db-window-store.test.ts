// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  effectiveRxWfWindow,
  useRxDbWindowStore,
} from './rx-db-window-store';
import { useDisplaySettingsStore } from './display-settings-store';
import { resetReceiverFloors, reportReceiverFloorDb } from '../dsp/floor-normalization';

beforeEach(() => {
  useRxDbWindowStore.setState({ overrides: {} });
  resetReceiverFloors();
  try {
    localStorage.removeItem('zeus.rxWfDbWindows');
  } catch {
    /* ignore */
  }
});
afterEach(() => {
  useRxDbWindowStore.setState({ overrides: {} });
  resetReceiverFloors();
});

describe('rx-db-window-store', () => {
  it('RX1 resolves to the raw global window when no floors are reported', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    expect(effectiveRxWfWindow(0)).toEqual({ wfDbMin: -130, wfDbMax: -40 });
  });

  it('every pane (incl. RX1) floor-normalizes toward the median floor', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    // Floors -135 (RX1) and -120 (RX2) → median -127.5. RX2 is +7.5 above it,
    // RX1 is -7.5 below it; both windows shift by that, symmetric about median.
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    const rx2 = effectiveRxWfWindow(1);
    expect(rx2.wfDbMin).toBeCloseTo(-122.5, 1);
    expect(rx2.wfDbMax).toBeCloseTo(-32.5, 1);
    const rx1 = effectiveRxWfWindow(0);
    expect(rx1.wfDbMin).toBeCloseTo(-137.5, 1);
    expect(rx1.wfDbMax).toBeCloseTo(-47.5, 1);
  });

  it('an explicit override wins over the normalized default', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    useRxDbWindowStore.getState().setRxWfWindow(1, -100, -20);
    expect(effectiveRxWfWindow(1)).toEqual({ wfDbMin: -100, wfDbMax: -20 });
  });

  it('shift creates an override from the current effective window, preserving span', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    useRxDbWindowStore.getState().shiftRxWfWindow(1, 10);
    const win = useRxDbWindowStore.getState().overrides[1]!;
    expect(win.wfDbMin).toBeCloseTo(-120, 1);
    expect(win.wfDbMax).toBeCloseTo(-30, 1);
    expect(win.wfDbMax - win.wfDbMin).toBeCloseTo(90, 1); // span preserved
  });

  it('clear drops the override back to the default', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    useRxDbWindowStore.getState().setRxWfWindow(2, -90, -10);
    expect(useRxDbWindowStore.getState().overrides[2]).toBeDefined();
    useRxDbWindowStore.getState().clearRxWfWindow(2);
    expect(useRxDbWindowStore.getState().overrides[2]).toBeUndefined();
    expect(effectiveRxWfWindow(2)).toEqual({ wfDbMin: -130, wfDbMax: -40 });
  });

  it('clearAll drops every override so all receivers re-normalize', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    useRxDbWindowStore.getState().setRxWfWindow(1, -100, -20);
    useRxDbWindowStore.getState().setRxWfWindow(2, -90, -10);
    useRxDbWindowStore.getState().clearAllRxWfWindows();
    expect(useRxDbWindowStore.getState().overrides).toEqual({});
    expect(effectiveRxWfWindow(1)).toEqual({ wfDbMin: -130, wfDbMax: -40 });
    expect(effectiveRxWfWindow(2)).toEqual({ wfDbMin: -130, wfDbMax: -40 });
    expect(JSON.parse(localStorage.getItem('zeus.rxWfDbWindows')!)).toEqual({});
  });

  it('ignores index 0 and inverted windows', () => {
    useRxDbWindowStore.getState().setRxWfWindow(0, -100, -20);
    useRxDbWindowStore.getState().setRxWfWindow(1, -20, -100); // inverted
    expect(useRxDbWindowStore.getState().overrides[0]).toBeUndefined();
    expect(useRxDbWindowStore.getState().overrides[1]).toBeUndefined();
  });

  it('persists overrides to localStorage', () => {
    useRxDbWindowStore.getState().setRxWfWindow(1, -110, -30);
    const raw = localStorage.getItem('zeus.rxWfDbWindows');
    expect(raw).toBeTruthy();
    expect(JSON.parse(raw!)).toMatchObject({ '1': { wfDbMin: -110, wfDbMax: -30 } });
  });
});
