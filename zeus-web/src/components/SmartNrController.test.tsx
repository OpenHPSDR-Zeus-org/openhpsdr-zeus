// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

const setNrMock = vi.hoisted(() => vi.fn<(nr: NrConfigDto, signal?: AbortSignal) => Promise<RadioStateDto>>());
const fetchHardwareDiagnosticsMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    fetchHardwareDiagnostics: fetchHardwareDiagnosticsMock,
    setNr: setNrMock,
  };
});

import {
  AGC_CONFIG_DEFAULT,
  CFC_CONFIG_DEFAULT,
  NR_CONFIG_DEFAULT,
  SQUELCH_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  type NrConfigDto,
  type RadioStateDto,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';
import { SmartNrController } from './SmartNrController';

const WIDTH = 256;
const NOISE_DB = -110;

function denseSsbNoise(): Float32Array {
  const spec = new Float32Array(WIDTH).fill(NOISE_DB);
  for (let i = 64; i < 192; i++) spec[i] = NOISE_DB + 12;
  return spec;
}

function weakCwSignal(): Float32Array {
  const spec = new Float32Array(WIDTH).fill(NOISE_DB);
  spec[120] = NOISE_DB + 14;
  return spec;
}

function quietNoise(): Float32Array {
  return new Float32Array(WIDTH).fill(NOISE_DB);
}

function resetRxMeters() {
  useRxMetersStore.setState({
    signalPk: -Infinity,
    signalAv: -Infinity,
    adcPk: -Infinity,
    adcAv: -Infinity,
    agcGain: 0,
    agcEnvPk: -Infinity,
    agcEnvAv: -Infinity,
  });
}

function mockState(nr: NrConfigDto): RadioStateDto {
  const conn = useConnectionStore.getState();
  return {
    status: 'Connected',
    endpoint: conn.endpoint,
    vfoHz: conn.vfoHz,
    mode: conn.mode,
    filterLowHz: conn.filterLowHz,
    filterHighHz: conn.filterHighHz,
    filterPresetName: conn.filterPresetName,
    filterAdvancedPaneOpen: conn.filterAdvancedPaneOpen,
    txFilterLowHz: conn.txFilterLowHz,
    txFilterHighHz: conn.txFilterHighHz,
    sampleRate: conn.sampleRate,
    agcTopDb: conn.agcTopDb,
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
    nr,
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

describe('SmartNrController', () => {
  let container: HTMLDivElement;
  let root: Root;
  let seq = 1;
  let now = 100_000;

  beforeEach(() => {
    seq = 1;
    now = 100_000;
    vi.useFakeTimers();
    vi.setSystemTime(now);
    setNrMock.mockReset();
    setNrMock.mockImplementation((nr) => Promise.resolve(mockState(nr)));
    fetchHardwareDiagnosticsMock.mockReset();
    fetchHardwareDiagnosticsMock.mockResolvedValue({
      dsp: {
        wdspActive: true,
        wdspEmnrPost2Available: true,
        wdspNr4SbnrAvailable: true,
      },
    });
    useSmartNrStore.getState().resetSettings();
    useSmartNrStore.getState().setSettings({ dwellSamples: 3 });
    useSmartNrStore.getState().setAutomationMode('auto');
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      nr: { ...NR_CONFIG_DEFAULT },
    });
    useTxStore.setState({
      moxOn: false,
      tunOn: false,
      rxDbm: -160,
    });
    resetRxMeters();
    useDisplayStore.setState({
      panDb: null,
      panValid: false,
      lastSeq: 0,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    act(() => {
      root.render(<SmartNrController />);
    });
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Disconnected',
      nr: { ...NR_CONFIG_DEFAULT },
    });
    useDisplayStore.setState({
      panDb: null,
      panValid: false,
      lastSeq: 0,
    });
    resetRxMeters();
    vi.useRealTimers();
  });

  function feed(spec: Float32Array) {
    vi.setSystemTime(now);
    act(() => {
      useDisplayStore.setState({
        panDb: spec,
        panValid: true,
        lastSeq: seq++,
      });
    });
    now += 1500;
  }

  async function flushPromises() {
    await act(async () => {
      await Promise.resolve();
    });
  }

  it('waits for a stable profile dwell before applying auto NR', () => {
    for (let i = 0; i < 5; i++) feed(denseSsbNoise());

    expect(setNrMock).not.toHaveBeenCalled();

    feed(denseSsbNoise());

    expect(setNrMock).toHaveBeenCalledTimes(1);
    expect(setNrMock.mock.calls[0]?.[0].nrMode).toBe('Emnr');
  });

  it('holds the current profile during the post-apply cooldown', () => {
    for (let i = 0; i < 6; i++) feed(denseSsbNoise());
    expect(setNrMock).toHaveBeenCalledTimes(1);

    for (let i = 0; i < 6; i++) feed(quietNoise());

    expect(setNrMock).toHaveBeenCalledTimes(1);
  });

  it('holds auto NR when the RX chain needs headroom first', () => {
    useRxMetersStore.setState({
      signalPk: -61,
      signalAv: -64,
      adcPk: -1.5,
      adcAv: -12,
      agcGain: -8,
      agcEnvPk: -62,
      agcEnvAv: -65,
    });

    for (let i = 0; i < 8; i++) feed(denseSsbNoise());

    expect(setNrMock).not.toHaveBeenCalled();
    expect(useSmartNrStore.getState().status?.heldByRxChain).toBe(true);
    expect(useSmartNrStore.getState().status?.rxChainLabel).toBe('ADC overload risk');
    expect(useSmartNrStore.getState().status?.rxChainRecommendation).toBe('Add 3-6 dB attenuation');
  });

  it('does not hold auto NR solely because signed AGC gain is cutting', () => {
    useRxMetersStore.setState({
      signalPk: -61,
      signalAv: -64,
      adcPk: -18,
      adcAv: -32,
      agcGain: -24,
      agcEnvPk: -62,
      agcEnvAv: -65,
    });

    for (let i = 0; i < 6; i++) feed(denseSsbNoise());

    expect(setNrMock).toHaveBeenCalledTimes(1);
    expect(useSmartNrStore.getState().status?.heldByRxChain).toBe(false);
    expect(useSmartNrStore.getState().status?.rxChainLabel).toBe('AGC stressed');
  });

  it('falls back to NR2 instead of applying NR4 when diagnostics reports SBNR unavailable', async () => {
    useConnectionStore.setState({ mode: 'CWU' });
    fetchHardwareDiagnosticsMock.mockResolvedValue({
      dsp: {
        wdspActive: true,
        wdspEmnrPost2Available: true,
        wdspNr4SbnrAvailable: false,
      },
    });

    feed(weakCwSignal());
    await flushPromises();
    for (let i = 0; i < 6; i++) feed(weakCwSignal());

    expect(setNrMock).toHaveBeenCalledTimes(1);
    expect(setNrMock.mock.calls[0]?.[0].nrMode).toBe('Emnr');
    expect(useSmartNrStore.getState().status?.capabilityLimited).toBe(true);
    expect(useSmartNrStore.getState().status?.capabilityRecommendation).toContain('NR4/SBNR unavailable');
  });
});
