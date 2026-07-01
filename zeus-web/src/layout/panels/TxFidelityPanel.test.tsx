// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  CFC_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  TX_PHASE_ROTATOR_CONFIG_DEFAULT,
  fetchTxDiagnostics,
  setCfcConfig,
  setDrive,
  setLevelerMaxGain,
  setMicGain,
  setTxLeveling,
  setTxPhaseRotator,
  type RadioStateDto,
  type TxDiagnosticsDto,
} from '../../api/client';
import { act, render } from '../../components/meters/__tests__/harness';
import { useAudioSuiteStore } from '../../state/audio-suite-store';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';
import { useTxAudioProfileStore } from '../../state/tx-audio-profile-store';
import { TxFidelityPanel } from './TxFidelityPanel';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    fetchTxFidelityPolicy: vi.fn(() =>
      Promise.resolve({ profileId: 'studio-ssb', targetSpectralDensity: 55 }),
    ),
    saveTxFidelityPolicy: vi.fn(async (policy: unknown) => policy),
    // Unified TX audio profile endpoints.
    fetchTxAudioProfiles: vi.fn(async () => [
      {
        id: 'studio-ssb',
        name: 'Studio SSB',
        micGainDb: 0,
        levelerMaxGainDb: 8,
        txLeveling: {
          alcMaxGainDb: 3,
          alcDecayMs: 10,
          levelerEnabled: true,
          levelerDecayMs: 100,
          compressorEnabled: false,
          compressorGainDb: 0,
        },
        cfcConfig: { enabled: false, postEqEnabled: false, preCompDb: 0, prePeqDb: 0, bands: [] },
        txPhaseRotator: { enabled: false, cornerHz: 338, stages: 8, reverse: false },
        lowCutHz: 150,
        highCutHz: 2850,
        processingMode: 'native' as const,
        masterBypass: false,
        chainOrder: [],
        chainParked: [],
        vstPluginStates: {},
        nativePluginStates: {},
        targetSpectralDensity: 55,
        createdUtc: '',
        updatedUtc: '',
      },
    ]),
    fetchLastLoadedTxAudioProfile: vi.fn(async () => ({ id: 'studio-ssb' })),
    fetchTxDiagnostics: vi.fn(),
    setMicGain: vi.fn(async (db: number) => ({ micGainDb: Math.round(db) })),
    setLevelerMaxGain: vi.fn(async (gain: number) => ({ levelerMaxGainDb: gain })),
    setCfcConfig: vi.fn(),
    setTxLeveling: vi.fn(),
    setTxPhaseRotator: vi.fn(),
    setDrive: vi.fn(async (drive: number) => ({ drivePercent: drive })),
  };
});

function previewEnabledFrom(init?: RequestInit): boolean {
  if (typeof init?.body !== 'string') return true;
  try {
    const parsed = JSON.parse(init.body) as { enabled?: unknown };
    return parsed.enabled !== false;
  } catch {
    return true;
  }
}

function requestPath(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.pathname;
  return input.url;
}

function makeTxDiag({
  totalTxBlocks = 1,
  micPkDbfs = -10,
  outPkDbfs = -7,
  outAvDbfs = -18,
  alcGrDb = 3,
  lvlrGrDb = 3,
  cfcGrDb = 2,
}: {
  totalTxBlocks?: number;
  micPkDbfs?: number;
  outPkDbfs?: number;
  outAvDbfs?: number;
  alcGrDb?: number;
  lvlrGrDb?: number;
  cfcGrDb?: number;
} = {}): TxDiagnosticsDto {
  return {
    ingest: { droppedFrames: 0, totalTxBlocks },
    protocol2: { queuedPackets: 0, sendFailures: 0, queueWriteFailures: 0 },
    stage: {
      micPkDbfs,
      outPkDbfs,
      outAvDbfs,
      compPkDbfs: outPkDbfs,
      compAvDbfs: outAvDbfs,
      alcGrDb,
      lvlrGrDb,
      cfcGrDb,
    },
    micUplink: { status: 'live' },
    vstEngine: { active: false, degradedBlocks: 0 },
  } as TxDiagnosticsDto;
}

function makeRadioState(): RadioStateDto {
  const conn = useConnectionStore.getState();
  const tx = useTxStore.getState();
  return {
    status: conn.status,
    endpoint: conn.endpoint,
    vfoHz: conn.vfoHz,
    rx2Enabled: conn.rx2Enabled,
    txVfo: conn.txVfo,
    txReceiverIndex: conn.txReceiverIndex,
    mode: conn.mode,
    filterLowHz: conn.filterLowHz,
    filterHighHz: conn.filterHighHz,
    filterPresetName: conn.filterPresetName,
    filterAdvancedPaneOpen: conn.filterAdvancedPaneOpen,
    txFilterLowHz: conn.txFilterLowHz,
    txFilterHighHz: conn.txFilterHighHz,
    rxFilterWindow: conn.rxFilterWindow,
    txFilterWindow: conn.txFilterWindow,
    sampleRate: conn.sampleRate,
    agcTopDb: conn.agcTopDb,
    agc: conn.agc,
    squelch: conn.squelch,
    txLeveling: conn.txLeveling,
    txPhaseRotator: conn.txPhaseRotator,
    autoAgcEnabled: conn.autoAgcEnabled,
    agcOffsetDb: conn.agcOffsetDb,
    rxAfGainDb: conn.rxAfGainDb,
    micGainDb: tx.micGainDb,
    levelerMaxGainDb: tx.levelerMaxGainDb,
    attenDb: conn.attenDb,
    autoAttEnabled: conn.autoAttEnabled,
    attOffsetDb: conn.attOffsetDb,
    adcOverloadWarning: conn.adcOverloadWarning,
    preampOn: conn.preampOn,
    nr: conn.nr,
    wdspNr3RnnrAvailable: conn.wdspNr3RnnrAvailable,
    nr3ModelName: conn.nr3ModelName,
    nr3UsingBundledDefault: conn.nr3UsingBundledDefault,
    zoomLevel: conn.zoomLevel,
    workspaceZoomPct: conn.workspaceZoomPct,
    psEnabled: false,
    psAuto: false,
    psPtol: false,
    psAutoAttenuate: false,
    psMoxDelaySec: 0,
    psLoopDelaySec: 0,
    psAmpDelayNs: 0,
    psHwPeak: 0,
    psHwPeakDefault: 0,
    psTxFeedbackAttenuationDb: 0,
    psTxFeedbackAttenuationDbMin: 0,
    psIntsSpiPreset: 'Default',
    psFeedbackSource: 'internal',
    txMonitorEnabled: tx.txMonitorEnabled,
    drivePercent: tx.drivePercent,
    tunePercent: tx.tunePercent,
    txMoxPreKeyDelayMs: tx.txMoxPreKeyDelayMs,
    txTimeoutSec: tx.txTimeoutSec,
    twoToneFreq1: tx.twoToneFreq1,
    twoToneFreq2: tx.twoToneFreq2,
    twoToneMag: tx.twoToneMag,
    cfc: tx.cfcConfig,
    radioLoHz: conn.radioLoHz,
    cwPitchHz: conn.cwPitchHz,
    ctunEnabled: conn.ctunEnabled,
    receivers: conn.receivers,
    maxReceivers: conn.maxReceivers,
    wireVersion: 2,
  } as RadioStateDto;
}

describe('TxFidelityPanel', () => {
  beforeEach(() => {
    useTxAudioProfileStore.setState({ profiles: [], loaded: false, lastLoadedId: null, busy: false });
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      txLeveling: { ...TX_LEVELING_CONFIG_DEFAULT },
      txPhaseRotator: { ...TX_PHASE_ROTATOR_CONFIG_DEFAULT },
    });
    useAudioSuiteStore.setState({
      masterBypassed: false,
      processingMode: 'native',
      previewSupported: true,
      previewEnabled: false,
      vstEngineActive: false,
    });
    useTxStore.setState({
      moxOn: false,
      tunOn: false,
      txMonitorEnabled: false,
      micGainDb: 0,
      levelerMaxGainDb: 8,
      drivePercent: 80,
      cfcConfig: {
        ...CFC_CONFIG_DEFAULT,
        bands: CFC_CONFIG_DEFAULT.bands.map((band) => ({ ...band })),
      },
      swr: 1.15,
      psFeedbackLevel: 0,
    });
    vi.mocked(fetchTxDiagnostics).mockResolvedValue(makeTxDiag());
    vi.mocked(setMicGain).mockImplementation(async (db: number) => ({ micGainDb: Math.round(db) }));
    vi.mocked(setLevelerMaxGain).mockImplementation(async (gain: number) => ({ levelerMaxGainDb: gain }));
    vi.mocked(setCfcConfig).mockResolvedValue({} as never);
    vi.mocked(setTxLeveling).mockResolvedValue({} as never);
    vi.mocked(setTxPhaseRotator).mockImplementation(async (txPhaseRotator) => {
      useConnectionStore.getState().setTxPhaseRotator(txPhaseRotator);
      return makeRadioState();
    });
    vi.mocked(setDrive).mockImplementation(async (drive: number) => ({ drivePercent: drive }));
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = requestPath(input);
        if (url === '/api/tx-audio-suite/preview') {
          const enabled = init?.method === 'PUT' ? previewEnabledFrom(init) : false;
          return new Response(JSON.stringify({ supported: true, enabled }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          });
        }
        if (url === '/api/tx-audio-suite/chain/meters') {
          return new Response(JSON.stringify({ outputDbfs: -7, outputDb: -7 }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          });
        }
        return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } });
      }),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('renders the unified TX Audio Profile bar and live shaping controls', async () => {
    const { container, unmount } = render(createElement(TxFidelityPanel));
    await act(async () => {
      for (let i = 0; i < 8; i++) await Promise.resolve();
    });

    // The shared profile bar is mounted (Save TX Audio Profile button).
    expect(container.querySelector('[aria-label="Save TX audio profile"]')).not.toBeNull();
    // Live shaping number boxes use the controlled-input pattern.
    expect(container.querySelector('[aria-label="TX mic gain"]')).not.toBeNull();
    expect(container.querySelector('[aria-label="TX leveler max gain"]')).not.toBeNull();
    expect(container.querySelector('[aria-label="TX filter low cut"]')).not.toBeNull();

    // The profile picker is populated from the unified store and shows the
    // last-loaded id as selected.
    const trigger = container.querySelector('[aria-label="TX audio profile"]') as HTMLButtonElement;
    expect(trigger).not.toBeNull();
    expect(trigger.textContent).toContain('Studio SSB [Native]');
    await act(async () => {
      trigger.click();
      for (let i = 0; i < 2; i++) await Promise.resolve();
    });
    const options = Array.from(container.querySelectorAll('[role="option"]')).map((o) =>
      o.textContent?.trim(),
    );
    expect(options).toContain('Studio SSB [Native]');

    unmount();
  });

  it('keeps tuning live, resamples after applying, and locks the final parameters', async () => {
    vi.useFakeTimers();
    let txBlocks = 0;
    let micApplied = false;
    vi.mocked(fetchTxDiagnostics).mockImplementation(async () => {
      txBlocks += 1;
      return micApplied
        ? makeTxDiag({ totalTxBlocks: txBlocks, micPkDbfs: -10, outPkDbfs: -7, outAvDbfs: -18 })
        : makeTxDiag({ totalTxBlocks: txBlocks, micPkDbfs: -20, outPkDbfs: -8, outAvDbfs: -19 });
    });
    vi.mocked(setMicGain).mockImplementation(async (db: number) => {
      micApplied = true;
      return { micGainDb: Math.round(db) };
    });
    useTxStore.setState({
      micGainDb: -12,
      levelerMaxGainDb: 6,
      txMonitorEnabled: false,
    });

    const { container, unmount } = render(createElement(TxFidelityPanel));
    await act(async () => {
      for (let i = 0; i < 8; i++) await Promise.resolve();
    });

    const button = container.querySelector('[aria-label="Auto tune TX fidelity"]') as HTMLButtonElement;
    expect(button).not.toBeNull();

    await act(async () => {
      button.click();
      await Promise.resolve();
      await vi.advanceTimersByTimeAsync(45_000);
    });

    expect(vi.mocked(setMicGain)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetchTxDiagnostics).mock.calls.length).toBeGreaterThan(20);
    expect(useTxStore.getState().micGainDb).toBeGreaterThan(-12);
    expect(useAudioSuiteStore.getState().previewEnabled).toBe(true);
    expect(container.textContent).toContain('Auto tune locked after 2 passes');

    unmount();
  });
});
