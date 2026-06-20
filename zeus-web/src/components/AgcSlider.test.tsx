// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

const setAgcThresholdMock = vi.hoisted(() =>
  vi.fn<(thresholdDbm: number, signal?: AbortSignal) => Promise<RadioStateDto>>(),
);

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setAgcThreshold: setAgcThresholdMock,
  };
});

import {
  AGC_CONFIG_DEFAULT,
  CFC_CONFIG_DEFAULT,
  NR_CONFIG_DEFAULT,
  SQUELCH_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  type RadioStateDto,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { AgcSlider } from './AgcSlider';

// A full StateDto echoing the requested knee — what the server returns from
// POST /api/agc/threshold. Only agcThresholdDbm is interesting to this test.
function mockState(thresholdDbm: number | null): RadioStateDto {
  const conn = useConnectionStore.getState();
  return {
    status: 'Connected',
    endpoint: conn.endpoint,
    vfoHz: conn.vfoHz,
    vfoBHz: conn.vfoBHz,
    rx2Enabled: false,
    rx2AudioMode: 'both',
    rx2AfGainDb: 0,
    txVfo: 'A',
    mode: conn.mode,
    modeB: conn.modeB,
    filterLowHz: conn.filterLowHz,
    filterHighHz: conn.filterHighHz,
    filterLowHzB: conn.filterLowHzB,
    filterHighHzB: conn.filterHighHzB,
    filterPresetName: conn.filterPresetName,
    filterPresetNameB: conn.filterPresetNameB,
    filterAdvancedPaneOpen: conn.filterAdvancedPaneOpen,
    txFilterLowHz: conn.txFilterLowHz,
    txFilterHighHz: conn.txFilterHighHz,
    sampleRate: conn.sampleRate,
    agcTopDb: conn.agcTopDb,
    agcThresholdDbm: thresholdDbm,
    agc: { ...AGC_CONFIG_DEFAULT },
    squelch: { ...SQUELCH_CONFIG_DEFAULT },
    txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT },
    autoAgcEnabled: false,
    agcOffsetDb: 0,
    rxAfGainDb: 0,
    micGainDb: 0,
    levelerMaxGainDb: 8,
    attenDb: 0,
    autoAttEnabled: true,
    attOffsetDb: 0,
    adcOverloadWarning: false,
    preampOn: false,
    nr: { ...NR_CONFIG_DEFAULT },
    zoomLevel: 1,
    psEnabled: false,
    psAuto: true,
    psPtol: false,
    psAutoAttenuate: true,
    psMoxDelaySec: 0,
    psLoopDelaySec: 0,
    psAmpDelayNs: 0,
    psHwPeak: 0.4072,
    psHwPeakDefault: 0.4072,
    psTxFeedbackAttenuationDb: 0,
    psTxFeedbackAttenuationDbMin: 0,
    psIntsSpiPreset: '16/256',
    psFeedbackSource: 'internal',
    txMonitorEnabled: false,
    drivePercent: 0,
    tunePercent: 10,
    txMoxPreKeyDelayMs: 0,
    twoToneFreq1: 700,
    twoToneFreq2: 1900,
    twoToneMag: 0.5,
    cfc: CFC_CONFIG_DEFAULT,
    radioLoHz: conn.vfoHz,
    cwPitchHz: 600,
    ctunEnabled: false,
  };
}

// The "Knee" range input is the one with aria-label "AGC threshold (knee)".
function kneeInput(container: HTMLElement): HTMLInputElement {
  const el = container.querySelector<HTMLInputElement>(
    'input[type="range"][aria-label="AGC threshold (knee)"]',
  );
  if (!el) throw new Error('Knee slider not found');
  return el;
}

// React tracks the previous value on the DOM node, so a plain `input.value =`
// is ignored as a no-op change. Go through the native value setter so React's
// onChange (bound to the native "input" event) actually fires.
function drag(input: HTMLInputElement, value: number) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    'value',
  )?.set;
  setter?.call(input, String(value));
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

describe('AgcSlider — Knee (AGC threshold)', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    vi.useFakeTimers();
    setAgcThresholdMock.mockReset();
    setAgcThresholdMock.mockImplementation((v) => Promise.resolve(mockState(v)));
    useConnectionStore.setState({
      status: 'Connected',
      agcThresholdDbm: null,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    act(() => {
      root.render(<AgcSlider />);
    });
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useConnectionStore.setState({
      status: 'Disconnected',
      agcThresholdDbm: null,
    });
    vi.useRealTimers();
  });

  it('shows "—" and the fallback thumb position while the knee is unset', () => {
    const input = kneeInput(container);
    expect(input.value).toBe('-120'); // KNEE_FALLBACK
    expect(container.textContent).toContain('—');
  });

  it('dragging the knee POSTs setAgcThreshold and applyState populates agcThresholdDbm', async () => {
    const input = kneeInput(container);

    act(() => {
      drag(input, -95);
    });

    // useLiveSlider coalesces on the next frame; jsdom falls back to setTimeout(0).
    await act(async () => {
      vi.runOnlyPendingTimers();
      await Promise.resolve();
    });

    expect(setAgcThresholdMock).toHaveBeenCalledTimes(1);
    expect(setAgcThresholdMock).toHaveBeenCalledWith(-95, expect.anything());

    // The echoed StateDto must have flowed through applyState into the store.
    expect(useConnectionStore.getState().agcThresholdDbm).toBe(-95);
  });

  it('flush on release commits the final value and the readout shows dBm', async () => {
    const input = kneeInput(container);

    act(() => {
      drag(input, -80);
      // Release before the rAF/timer tick — flush() must still land the value.
      input.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    });
    await act(async () => {
      await Promise.resolve();
    });

    expect(setAgcThresholdMock).toHaveBeenCalledWith(-80, expect.anything());
    expect(useConnectionStore.getState().agcThresholdDbm).toBe(-80);
    expect(container.textContent).toContain('-80 dBm');
  });

  it('the knee slider is disabled when not connected', () => {
    act(() => {
      useConnectionStore.setState({ status: 'Disconnected' });
    });
    expect(kneeInput(container).disabled).toBe(true);
  });
});
