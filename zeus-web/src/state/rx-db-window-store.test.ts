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
  it('RX1 (index 0) always resolves to the global window', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    expect(effectiveRxWfWindow(0)).toEqual({ wfDbMin: -130, wfDbMax: -40 });
  });

  it('an RXn without override floor-normalizes the global window to RX1', () => {
    useDisplaySettingsStore.setState({ wfDbMin: -130, wfDbMax: -40 });
    // RX1 floor -135, RX2 floor -120 → RX2 is 15 dB hotter → window shifts +15.
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    const win = effectiveRxWfWindow(1);
    expect(win.wfDbMin).toBeCloseTo(-115, 1);
    expect(win.wfDbMax).toBeCloseTo(-25, 1);
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
