// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { afterEach, describe, expect, it, vi } from 'vitest';
import type {
  AgcConfigDto,
  CfcConfigDto,
  SquelchConfigDto,
  TxLevelingConfigDto,
} from './client';
import {
  AGC_CONFIG_DEFAULT,
  ApiError,
  CFC_CONFIG_DEFAULT,
  connect,
  NR_CONFIG_DEFAULT,
  SQUELCH_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  createHardwareDiagnosticsMarker,
  fetchCfcPresets,
  fetchExternalPttStatus,
  fetchFrontendAudioPlaybackDiagnostics,
  fetchFrontendDspSceneDiagnostics,
  fetchDspLiveDiagnostics,
  fetchG2SensorMappingDiagnostics,
  fetchHardwareDiagnostics,
  fetchHardwareKeyingStatus,
  fetchRadioDigInDiagnostics,
  fetchRadioNetworkProfile,
  fetchRadioPaThermalDiagnostics,
  fetchRadioPowerCalibration,
  fetchRadioSupplyAlarms,
  fetchSmartNrCondition,
  fetchTxDiagnostics,
  fetchUserIoActions,
  fetchUserIoLabels,
  fetchAdcProtection,
  fetchTxFidelityPolicy,
  publishFrontendAudioPlaybackDiagnostics,
  publishFrontendDspSceneDiagnostics,
  normalizeAgc,
  normalizeAdcProtectionStatus,
  normalizeAgcMode,
  normalizeMode,
  normalizeNbMode,
  normalizeNr,
  normalizeNrMode,
  normalizeSquelch,
  normalizeState,
  normalizeStatus,
  normalizeTxVfo,
  normalizeTxLeveling,
  setAgc,
  setAgcTop,
  setSquelch,
  setTxLeveling,
  setAttenuator,
  setAutoAtt,
  setAdcProtection,
  saveTxFidelityPolicy,
  saveCfcPreset,
  setLevelerMaxGain,
  setFilter,
  setMicGain,
  setMode,
  setNr,
  setPreamp,
  setSampleRate,
  setTun,
  setTxVfo,
  setZoom,
} from './client';

describe('normalizeStatus', () => {
  it('accepts string values', () => {
    expect(normalizeStatus('Connected')).toBe('Connected');
    expect(normalizeStatus('Disconnected')).toBe('Disconnected');
    expect(normalizeStatus('Connecting')).toBe('Connecting');
    expect(normalizeStatus('Error')).toBe('Error');
  });
  it('maps numeric enum values by position', () => {
    expect(normalizeStatus(0)).toBe('Disconnected');
    expect(normalizeStatus(1)).toBe('Connecting');
    expect(normalizeStatus(2)).toBe('Connected');
    expect(normalizeStatus(3)).toBe('Error');
  });
  it('falls back to Error for unknown input', () => {
    expect(normalizeStatus(99)).toBe('Error');
    expect(normalizeStatus('Bogus')).toBe('Error');
    expect(normalizeStatus(null)).toBe('Error');
  });
});

describe('normalizeMode', () => {
  it('accepts string values', () => {
    expect(normalizeMode('USB')).toBe('USB');
    expect(normalizeMode('DIGU')).toBe('DIGU');
  });
  it('maps numeric enum values to RxMode in Zeus.Contracts order', () => {
    expect(normalizeMode(0)).toBe('LSB');
    expect(normalizeMode(1)).toBe('USB');
    expect(normalizeMode(4)).toBe('AM');
    expect(normalizeMode(9)).toBe('DIGU');
    expect(normalizeMode(10)).toBe('FREEDV');
  });
  it('matches the server enum name case-insensitively (FreeDv → FREEDV)', () => {
    // The server serialises RxMode by its C# enum name: all-caps for every mode
    // except FreeDv (PascalCase). Without case-insensitive matching, "FreeDv"
    // missed MODE_ORDER and normalizeMode fell back to USB, making FreeDV mode
    // silently revert to USB on the next state round-trip.
    expect(normalizeMode('FreeDv')).toBe('FREEDV');
    expect(normalizeMode('FREEDV')).toBe('FREEDV');
    expect(normalizeMode('usb')).toBe('USB');
  });
  it('falls back to USB on garbage', () => {
    expect(normalizeMode('nope')).toBe('USB');
    expect(normalizeMode(42)).toBe('USB');
  });
});

describe('normalizeTxVfo', () => {
  it('accepts numeric and string enum values', () => {
    expect(normalizeTxVfo(0)).toBe('A');
    expect(normalizeTxVfo(1)).toBe('B');
    expect(normalizeTxVfo('A')).toBe('A');
    expect(normalizeTxVfo('b')).toBe('B');
  });
  it('falls back to A on garbage', () => {
    expect(normalizeTxVfo(42)).toBe('A');
    expect(normalizeTxVfo('nope')).toBe('A');
  });
});

describe('normalizeState', () => {
  it('reads a camelCase StateDto with numeric enums', () => {
    const s = normalizeState({
      status: 2,
      endpoint: '192.168.100.21:1024',
      vfoHz: 14_200_000,
      vfoBHz: 14_250_000,
      rx2Enabled: true,
      rx2AfGainDb: -3,
      txVfo: 1,
      mode: 1,
      modeB: 3,
      filterLowHz: 150,
      filterHighHz: 2850,
      filterLowHzB: 475,
      filterHighHzB: 725,
      filterPresetNameB: 'F6',
      sampleRate: 192_000,
    });
    expect(s.status).toBe('Connected');
    expect(s.mode).toBe('USB');
    expect(s.endpoint).toBe('192.168.100.21:1024');
    expect(s.vfoHz).toBe(14_200_000);
    // RX2 (*B) fields no longer surface on RadioStateDto — they live in
    // receivers[1]. The server may still send them; normalizeState ignores them.
    expect(s.rx2Enabled).toBe(true);
    expect(s.txVfo).toBe('B');
    expect(s.sampleRate).toBe(192_000);
    expect(s.preampOn).toBe(false);
  });
  it('reads a StateDto with string enums', () => {
    const s = normalizeState({
      status: 'Disconnected',
      endpoint: null,
      vfoHz: 7_100_000,
      mode: 'LSB',
      filterLowHz: -2850,
      filterHighHz: -150,
      sampleRate: 48_000,
    });
    expect(s.status).toBe('Disconnected');
    expect(s.mode).toBe('LSB');
    expect(s.endpoint).toBe(null);
  });
  it('coerces missing fields to safe defaults', () => {
    const s = normalizeState({});
    expect(s.status).toBe('Error');
    expect(s.endpoint).toBe(null);
    expect(s.vfoHz).toBe(0);
    expect(s.rx2Enabled).toBe(false);
    expect(s.txVfo).toBe('A');
    expect(s.mode).toBe('USB');
    expect(s.nr).toEqual(NR_CONFIG_DEFAULT);
    expect(s.zoomLevel).toBe(1);
  });
  it('carries the multi-DDC receivers[] array and ceiling (wire v2)', () => {
    // Regression: normalizeState used to drop receivers/maxReceivers/wireVersion,
    // so applyState's `s.receivers ?? prev.receivers` kept the empty default and
    // the "exposed receivers" control was frozen at RX1 (>2 did nothing).
    const s = normalizeState({
      wireVersion: 2,
      maxReceivers: 8,
      receivers: [
        { index: 0, enabled: true, adcSource: 0, vfoHz: 7_100_000, mode: 'LSB' },
        { index: 1, enabled: true, adcSource: 0, vfoHz: 7_150_000, mode: 'USB' },
        { index: 2, enabled: true, adcSource: 1, vfoHz: 14_200_000, mode: 'USB' },
      ],
    });
    expect(s.wireVersion).toBe(2);
    expect(s.maxReceivers).toBe(8);
    expect(s.receivers).toHaveLength(3);
    expect(s.receivers?.[2]).toMatchObject({ index: 2, enabled: true, adcSource: 1, mode: 'USB' });
  });
  it('leaves receivers/maxReceivers undefined when a v1 server omits them', () => {
    // Undefined (not []/0) lets applyState keep the store's prior values rather
    // than collapsing the exposed-receiver control.
    const s = normalizeState({});
    expect(s.receivers).toBeUndefined();
    expect(s.maxReceivers).toBeUndefined();
    expect(s.wireVersion).toBeUndefined();
  });
  it('reads zoomLevel from the server', () => {
    expect(normalizeState({ zoomLevel: 3 }).zoomLevel).toBe(3);
    expect(normalizeState({ zoomLevel: 4 }).zoomLevel).toBe(4);
    expect(normalizeState({ zoomLevel: 8 }).zoomLevel).toBe(8);
    expect(normalizeState({ zoomLevel: 16 }).zoomLevel).toBe(16);
    expect(normalizeState({ zoomLevel: 32 }).zoomLevel).toBe(32);
  });
  it('clamps out-of-range zoomLevel to 1', () => {
    expect(normalizeState({ zoomLevel: 0 }).zoomLevel).toBe(1);
    expect(normalizeState({ zoomLevel: 33 }).zoomLevel).toBe(1);
    expect(normalizeState({ zoomLevel: 1.5 }).zoomLevel).toBe(1);
    expect(normalizeState({ zoomLevel: 'lots' }).zoomLevel).toBe(1);
  });
  it('defaults auto-ATT fields when missing (server-default ON)', () => {
    const s = normalizeState({});
    expect(s.autoAttEnabled).toBe(true);
    expect(s.attOffsetDb).toBe(0);
    expect(s.adcOverloadWarning).toBe(false);
    expect(s.preampOn).toBe(false);
  });
  it('reads persisted PureSignal arm state from the server', () => {
    expect(normalizeState({ psEnabled: true }).psEnabled).toBe(true);
    expect(normalizeState({ psEnabled: 'yes' }).psEnabled).toBe(false);
  });
  it('reads preamp state from the server', () => {
    expect(normalizeState({ preampOn: true }).preampOn).toBe(true);
  });
  it('reads auto-ATT fields from the server', () => {
    const s = normalizeState({
      autoAttEnabled: false,
      attOffsetDb: 12,
      adcOverloadWarning: true,
    });
    expect(s.autoAttEnabled).toBe(false);
    expect(s.attOffsetDb).toBe(12);
    expect(s.adcOverloadWarning).toBe(true);
  });
  it('reads an NrConfig block with string enums', () => {
    const s = normalizeState({
      status: 'Connected',
      mode: 'USB',
      nr: {
        nrMode: 'Emnr',
        anfEnabled: true,
        snbEnabled: false,
        nbpNotchesEnabled: true,
        nbMode: 'Nb2',
        nbThreshold: 42,
      },
    });
    expect(s.nr.nrMode).toBe('Emnr');
    expect(s.nr.anfEnabled).toBe(true);
    expect(s.nr.nbMode).toBe('Nb2');
    expect(s.nr.nbThreshold).toBe(42);
  });
});

describe('normalizeNrMode / normalizeNbMode', () => {
  it('accepts string forms', () => {
    expect(normalizeNrMode('Anr')).toBe('Anr');
    expect(normalizeNrMode('Emnr')).toBe('Emnr');
    expect(normalizeNrMode('Unsupported')).toBe('Off');
    expect(normalizeNbMode('Nb1')).toBe('Nb1');
  });
  it('maps numeric ordinals', () => {
    expect(normalizeNrMode(0)).toBe('Off');
    expect(normalizeNrMode(1)).toBe('Anr');
    expect(normalizeNrMode(2)).toBe('Emnr');
    expect(normalizeNrMode(3)).toBe('Sbnr');
    expect(normalizeNrMode(4)).toBe('Rnnr');
    // 5 is past the last defined ordinal (Rnnr = 4) → Off.
    expect(normalizeNrMode(5)).toBe('Off');
    expect(normalizeNbMode(2)).toBe('Nb2');
  });
  it('falls back to Off on garbage', () => {
    expect(normalizeNrMode('nope')).toBe('Off');
    expect(normalizeNbMode(99)).toBe('Off');
  });
});

describe('normalizeNr', () => {
  it('returns defaults for null/undefined', () => {
    expect(normalizeNr(null)).toEqual(NR_CONFIG_DEFAULT);
    expect(normalizeNr(undefined)).toEqual(NR_CONFIG_DEFAULT);
  });

});

describe('POST helpers', () => {
  afterEach(() => vi.unstubAllGlobals());

  const okState = {
    status: 'Connected',
    endpoint: '192.168.100.21:1024',
    vfoHz: 14_200_000,
    mode: 'USB',
    filterLowHz: 150,
    filterHighHz: 2850,
    sampleRate: 192_000,
  };

  const jsonResponse = (body: unknown, status = 200): Response =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });

  it('connect serializes optional preampOn/atten', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await connect({
      endpoint: '192.168.100.21:1024',
      sampleRate: 192_000,
      preampOn: true,
      atten: 2,
    });

    const [, init] = fetchMock.mock.calls[0]!;
    const body = JSON.parse((init?.body ?? '') as string);
    expect(body).toEqual({
      endpoint: '192.168.100.21:1024',
      sampleRate: 192_000,
      preampOn: true,
      atten: 2,
    });
  });

  it('setSampleRate posts { rate } to /api/sampleRate', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setSampleRate(384_000);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/sampleRate');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ rate: 384_000 });
  });

  it('setMode posts numeric enum ordinal to /api/mode', async () => {
    // Server accepts enums only as numbers; string form is a 400. Guard
    // against accidental regression by asserting the serialized form.
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockImplementation(async () => jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setMode('LSB');
    expect(JSON.parse((fetchMock.mock.calls[0]![1]?.body ?? '') as string))
      .toEqual({ mode: 0, receiver: 0 });

    await setMode('DIGU');
    expect(JSON.parse((fetchMock.mock.calls[1]![1]?.body ?? '') as string))
      .toEqual({ mode: 9, receiver: 0 });

    await setMode('CWU', undefined, 'B');
    expect(JSON.parse((fetchMock.mock.calls[2]![1]?.body ?? '') as string))
      .toEqual({ mode: 3, receiver: 1 });
  });

  it('setFilter posts receiver to /api/filter', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockImplementation(async () => jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setFilter(475, 725, 'F6', undefined, 'B');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/filter');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      lowHz: 475,
      highHz: 725,
      presetName: 'F6',
      receiver: 1,
    });
  });

  it('setPreamp posts { on } to /api/preamp', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setPreamp(true);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/preamp');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ on: true });
  });

  it('setAgcTop posts { topDb } to /api/agcGain', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setAgcTop(95);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/agcGain');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ topDb: 95 });
  });

  it('setNr posts { nr } with string enums to /api/rx/nr', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setNr({
      nrMode: 'Emnr',
      anfEnabled: true,
      snbEnabled: false,
      nbpNotchesEnabled: true,
      nbMode: 'Off',
      nbThreshold: 20,
    });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/nr');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      nr: {
        nrMode: 'Emnr',
        anfEnabled: true,
        snbEnabled: false,
        nbpNotchesEnabled: true,
        nbMode: 'Off',
        nbThreshold: 20,
      },
    });
  });

  it('setZoom posts { level } to /api/rx/zoom', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setZoom(4);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/zoom');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ level: 4 });
  });

  it('setAttenuator posts { db } to /api/attenuator', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setAttenuator(20);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/attenuator');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ db: 20 });
  });

  it('setAutoAtt posts { enabled } to /api/auto-att', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    await setAutoAtt(false);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/auto-att');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ enabled: false });
  });

  it('normalizes ADC protection status with safe defaults', () => {
    const status = normalizeAdcProtectionStatus({
      config: {
        enabled: false,
        attackMs: 75,
        releaseMs: 600,
        attackStepDb: 2,
        releaseStepDb: 1,
        maxOffsetDb: 18,
        warningThreshold: 2,
        magnitudeSoftLimit: 52000,
      },
      attenDb: 3,
      offsetDb: 4,
      effectiveDb: 7,
      warning: true,
      overloadLevel: 5,
      lastOverloadBits: 3,
      adc0MaxMagnitude: 44000,
      adc1MaxMagnitude: null,
      adc0MaxMagnitudeAtOverload: 50000,
      adc1MaxMagnitudeAtOverload: 0,
      lastTelemetryUtc: '2026-06-14T22:00:00Z',
    });

    expect(status.config.enabled).toBe(false);
    expect(status.config.attackMs).toBe(75);
    expect(status.effectiveDb).toBe(7);
    expect(status.warning).toBe(true);
    expect(status.adc0MaxMagnitude).toBe(44000);
    expect(status.adc1MaxMagnitude).toBeNull();
  });

  it('fetchAdcProtection reads /api/rx/adc-protection', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({}));
    vi.stubGlobal('fetch', fetchMock);

    await fetchAdcProtection();
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/adc-protection');
    expect(init?.method ?? 'GET').toBe('GET');
  });

  it('fetchTxFidelityPolicy normalizes the active TX policy', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      profileId: 'DX',
      targetSpectralDensity: 101,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const policy = await fetchTxFidelityPolicy();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/fidelity-policy');
    expect(init?.method ?? 'GET').toBe('GET');
    expect(policy).toEqual({
      profileId: 'dx',
      targetSpectralDensity: 100,
    });
  });

  it('saveTxFidelityPolicy puts profile id and density target', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      profileId: 'essb',
      targetSpectralDensity: 88,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const policy = await saveTxFidelityPolicy({
      profileId: 'essb',
      targetSpectralDensity: 88,
    });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/fidelity-policy');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      profileId: 'essb',
      targetSpectralDensity: 88,
    });
    expect(policy).toEqual({
      profileId: 'essb',
      targetSpectralDensity: 88,
    });
  });

  it('setAdcProtection puts the partial config to /api/rx/adc-protection', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({}));
    vi.stubGlobal('fetch', fetchMock);

    await setAdcProtection({ enabled: true, attackMs: 50, maxOffsetDb: 20 });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/adc-protection');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      enabled: true,
      attackMs: 50,
      maxOffsetDb: 20,
    });
  });

  it('createHardwareDiagnosticsMarker posts label/notes and normalizes the API catalog', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({
        hardwareDiagnosticsApiVersion: 1,
        connectionStatus: 'Connected',
        mode: 'USB',
        dsp: {
          schemaVersion: 1,
          engine: 'WdspDspEngine',
          engineKind: 'WDSP',
          wdspActive: true,
          wdspNativeLoadable: true,
          wdspEmnrPost2Available: true,
          wdspNr4SbnrAvailable: false,
          nr4Readiness: 'missing-sbnr-exports',
          requestedNrMode: 'Sbnr',
          effectiveNrMode: 'Off',
          channelId: 1,
          sampleRateHz: 192000,
          displayWidth: 2048,
          tickRateHz: 30,
          audioOutputRateHz: 48000,
          txBlockSamples: 1024,
          txOutputSamples: 4096,
          txMonitorRequested: true,
          rxSinkAttached: true,
          audioSinkCount: 1,
          monitorBacklogSamples: 0,
          rxDsp: {
            schemaVersion: 1,
            status: 'nr-capability-limited',
            mode: 'USB',
            filterLowHz: 150,
            filterHighHz: 2850,
            filterPresetName: 'F6',
            agcMode: 'Fast',
            agcTopDb: 68,
            autoAgcEnabled: true,
            agcOffsetDb: -6,
            effectiveAgcTopDb: 62,
            squelchEnabled: true,
            squelchAdaptive: true,
            squelchLevel: 18,
            requestedNrMode: 'Sbnr',
            effectiveNrMode: 'Off',
            anfEnabled: true,
            snbEnabled: true,
            nbpNotchesEnabled: false,
            effectiveNbpNotchesRun: true,
            nbMode: 'Nb1',
            nbThreshold: 18,
            manualNotchCount: 2,
            activeManualNotchCount: 1,
            wdspActive: true,
            wdspNativeLoadable: true,
            wdspEmnrPost2Available: true,
            wdspNr4SbnrAvailable: false,
            nr4Readiness: 'missing-sbnr-exports',
            appliedNrMatchesRequested: true,
            appliedAgcMatchesRequested: true,
            appliedSquelchMatchesRequested: true,
            activeFeatures: ['anf', 'snb', 'manual-notches', 'nb1', 'auto-agc'],
            qualityReasons: ['wdsp-active', 'nr-requested-not-effective', 'nr-capability-limited'],
            diagnosticRecommendation: 'Use NR2/EMNR until NR4/SBNR exports are available.',
          },
          rxMeters: {
            schemaVersion: 1,
            status: 'adc-hot',
            source: 'wdsp-rxa-meter-ring',
            fresh: true,
            stale: false,
            ageMs: 250,
            channelId: 1,
            rxDbm: -44.6,
            signalPkDbm: -38.2,
            signalAvDbm: -49,
            adcPkDbfs: -1.8,
            adcAvDbfs: -14.3,
            adcHeadroomDb: 1.8,
            agcGainDb: -9.5,
            agcEnvPkDbm: -35,
            agcEnvAvDbm: -48,
            signalUsable: true,
            adcUsable: true,
            agcEnvelopeUsable: true,
            diagnosticRecommendation: 'RX ADC peak is within 3 dB of full scale.',
          },
          rxDynamicRange: {
            schemaVersion: 1,
            status: 'adc-headroom-limited',
            tone: 'danger',
            fresh: true,
            stale: false,
            ageMs: 250,
            source: 'rx-meters+radio-state+adc-protection',
            sampleRateHz: 192000,
            attenDb: 3,
            attOffsetDb: 2,
            effectiveAttenDb: 5,
            preampOn: true,
            autoAttEnabled: true,
            adcProtectionEnabled: true,
            adcOverloadWarning: true,
            adcOverloadLevel: 4,
            targetHeadroomMinDb: 6,
            targetHeadroomMaxDb: 30,
            rxDbm: -44.6,
            signalPkDbm: -38.2,
            adcPkDbfs: -1.8,
            adcHeadroomDb: 1.8,
            agcGainDb: -9.5,
            headroomOptimal: false,
            overloadRisk: true,
            weakSignalOpportunity: false,
            frontEndUnderused: false,
            reasons: ['adc-overload-warning'],
            actions: [
              {
                id: 'add-attenuation',
                label: 'Add 3-6 dB attenuation',
                status: 'auto-or-manual',
                notes: 'Auto-ATT is enabled.',
              },
              {
                id: 'disable-preamp',
                label: 'Disable preamp',
                status: 'candidate',
                notes: 'The preamp is on while ADC headroom is limited.',
              },
            ],
            diagnosticRecommendation: 'ADC headroom is limited; protect the converter first.',
          },
          audio: {
            schemaVersion: 1,
            status: 'fresh',
            source: 'rx',
            fresh: true,
            stale: false,
            ageMs: 35,
            framesBroadcast: 128,
            lastSeq: 128,
            sampleRateHz: 48000,
            sampleCount: 1600,
            rmsLinear: 0.031623,
            peakLinear: 0.18,
            rmsDbfs: -30,
            peakDbfs: -14.9,
            txMonitorRequested: false,
            squelchEnabled: true,
            squelchOpen: true,
            squelchTailActive: false,
            squelchGateGain: 1,
            squelchMode: 'adaptive',
            squelchGateSource: 'backend-adaptive',
            squelchOpenKnown: true,
            monitorBacklogSamples: 0,
            audioSinkCount: 1,
            diagnosticRecommendation: 'RX audio frames are fresh.',
          },
          listenability: {
            schemaVersion: 1,
            status: 'audio-recovered',
            tone: 'ready',
            signalPresent: true,
            audioRecovered: true,
            blocker: 'none',
            recommendation: 'RX signal evidence and recovered audio agree.',
          },
          display: {
            schemaVersion: 1,
            status: 'fresh',
            clientCount: 1,
            framesBroadcast: 42,
            lastSeq: 42,
            lastFrameAgeMs: 35,
            lastFrameUnixMs: 1781539200000,
            panValid: true,
            waterfallValid: true,
            panSource: 'rx',
            waterfallSource: 'rx',
            keyed: false,
            psMonitorRequested: false,
            psFeedbackCorrecting: false,
            width: 2048,
            centerHz: 7262000,
            hzPerPixel: 187.5,
            pan: {
              valid: true,
              ageMs: 35,
              validBins: 2048,
              minDb: -126.4,
              maxDb: -71.2,
              meanDb: -112.5,
              dynamicRangeDb: 55.2,
            },
            waterfall: {
              valid: true,
              ageMs: 35,
              validBins: 2048,
              minDb: -128.2,
              maxDb: -73.8,
              meanDb: -113.1,
              dynamicRangeDb: 54.4,
            },
            diagnosticRecommendation: 'Display analyzer frames are fresh.',
          },
          filterGeometry: {
            schemaVersion: 1,
            status: 'runtime-rate-writable-fixed-profile',
            operatorConfigurable: true,
            hardwareLimits: {
              rxAdcCount: 2,
              maxRxSampleRateHz: 1536000,
              activeSampleRateHz: 192000,
              sampleRates: [
                { sampleRateHz: 48000, label: '48 kHz', boardSupported: true, protocol2Required: false, active: false, status: 'hardware-supported' },
                { sampleRateHz: 96000, label: '96 kHz', boardSupported: true, protocol2Required: false, active: false, status: 'hardware-supported' },
                { sampleRateHz: 192000, label: '192 kHz', boardSupported: true, protocol2Required: false, active: true, status: 'hardware-supported' },
                { sampleRateHz: 384000, label: '384 kHz', boardSupported: true, protocol2Required: false, active: false, status: 'hardware-supported' },
                { sampleRateHz: 768000, label: '768 kHz', boardSupported: true, protocol2Required: true, active: false, status: 'hardware-supported-p2-only' },
                { sampleRateHz: 1536000, label: '1536 kHz', boardSupported: true, protocol2Required: true, active: false, status: 'hardware-supported-p2-only' },
              ],
            },
            runtimeSampleRateControl: {
              status: 'wideband-control-ready',
              writable: true,
              requiresReconnect: false,
              activeSampleRateHz: 192000,
              maxBoardSampleRateHz: 1536000,
              maxWritableSampleRateHz: 1536000,
              protocol2Active: true,
              widebandWritable: true,
              settingsSurface: 'Settings > DSP > Bandwidth',
              apiRoute: '/api/sampleRate',
              diagnosticRecommendation: 'Wider 768/1536 kHz spans are available now.',
            },
            receiverBandwidth: {
              schemaVersion: 1,
              status: 'wideband-underused',
              tone: 'ready',
              connected: true,
              protocol2Active: true,
              p2WidebandCapable: true,
              widebandActive: false,
              activeSampleRateHz: 192000,
              maxSampleRateHz: 1536000,
              activeNyquistHz: 96000,
              maxNyquistHz: 768000,
              utilizationPct: 12.5,
              unusedSampleRateHz: 1344000,
              unusedNyquistHz: 672000,
              activeSoftwareReceivers: 1,
              manualReceiverCapacity: 10,
              unexposedReceiverCount: 9,
              activeUserDdcIndex: 2,
              activeSlots: [
                {
                  slot: 2,
                  purpose: 'RX1',
                  status: 'active',
                  notes: 'Primary operator receive DDC.',
                },
              ],
              reservedSlots: [
                {
                  slot: 0,
                  purpose: 'PureSignal RX feedback',
                  status: 'reserved',
                  notes: 'Post-PA feedback.',
                },
                {
                  slot: 1,
                  purpose: 'PureSignal TX reference',
                  status: 'reserved',
                  notes: 'TX-DAC reference.',
                },
              ],
              source: 'ANAN G2 manual receiver architecture + Protocol2Client DDC map + BoardCapabilities',
              diagnosticRecommendation: 'Receiver hardware has unused DDC bandwidth.',
            },
            optionCatalog: {
              iqBufferSizes: [64, 128, 256, 512, 1024],
              filterTapSizes: [64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144],
              filterTypes: ['Linear Phase', 'Low Latency'],
              filterWindows: [
                { id: 0, label: 'BH-4', notes: 'Thetis default in DSP Options; sharper transition.' },
                { id: 1, label: 'BH-7', notes: 'Deeper cutoff; this is the current Zeus WDSP call.' },
              ],
              slowModeChangeWarning: 'Thetis warns that different buffer sizes, tap sizes, or filter types can force a slow mode change.',
              source: 'Thetis DSP Options mode defaults + Zeus WDSPwisdom 64..262144 startup planning ladder',
            },
            activeRx: {
              mode: 'USB',
              filterLowHz: 150,
              filterHighHz: 2850,
              filterPresetName: 'F6',
              inputBufferSize: 1024,
              dspBufferSize: 1024,
              filterWindowId: 1,
              filterWindow: 'BH-7',
              filterType: 'Low Latency',
              filterTaps: null,
              status: 'wired-fixed',
            },
            activeTx: {
              mode: 'USB',
              filterLowHz: 150,
              filterHighHz: 2850,
              inputBufferSize: 512,
              dspBufferSize: 1024,
              outputBufferSize: 2048,
              filterWindowId: 1,
              filterWindow: 'BH-7',
              filterType: 'profile-fixed',
              filterTaps: null,
              cfirCompensation: true,
              status: 'wired-fixed',
            },
            thetisMatrix: [
              { modeFamily: 'SSB/AM', direction: 'RX', iqBufferSize: 1024, filterTaps: 16384, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'SSB/AM', direction: 'TX', iqBufferSize: 1024, filterTaps: 16384, filterType: 'Linear Phase', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'FM', direction: 'RX', iqBufferSize: 256, filterTaps: 4096, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'FM', direction: 'TX', iqBufferSize: 128, filterTaps: 1024, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'CW', direction: 'RX', iqBufferSize: 64, filterTaps: 4096, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'CW', direction: 'TX', iqBufferSize: null, filterTaps: null, filterType: 'Mode generated', filterWindow: 'BH-4', status: 'reference-no-separate-tx-row' },
              { modeFamily: 'Digital', direction: 'RX', iqBufferSize: 64, filterTaps: 4096, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
              { modeFamily: 'Digital', direction: 'TX', iqBufferSize: 64, filterTaps: 4096, filterType: 'Low Latency', filterWindow: 'BH-4', status: 'reference' },
            ],
            impulseCache: {
              fftwWisdomPhase: 'Ready',
              fftwWisdomStatus: '',
              fftwWisdomCache: true,
              filterImpulseCache: false,
              saveRestoreImpulseCacheFile: false,
              status: 'fftw-wisdom-only',
              notes: 'Zeus initializes WDSP FFTW wisdom at startup.',
            },
            highResolutionFilterDisplay: {
              enabled: false,
              status: 'not-exposed-as-filter-display-setting',
              notes: 'Zeus exposes live filter edges.',
            },
            diagnosticRecommendation: 'All verified hardware sample-rate sizes and Thetis DSP option sizes are now visible.',
            source: 'Thetis DSP Options filter matrix + Zeus WdspDspEngine profile',
          },
          wdspWisdomPhase: 'Ready',
          wdspWisdomStatus: '',
          readiness: 'wdsp-active',
        },
        frontendDspScene: {
          schemaVersion: 1,
          available: true,
          ageMs: 450,
          status: 'fresh',
          fresh: true,
          stale: false,
          diagnosticRecommendation: 'Frontend DSP scene telemetry is fresh and ready for remote diagnostics.',
          atUtc: '2026-06-15T01:00:00Z',
          sourceClientId: 'frontend-test',
          mode: 'USB',
          signalProfile: 'dx',
          signalReason: 'sparse weak signal',
          smartNrProfile: 'NR2',
          smartNrReason: 'SSB noise profile',
          smartNrRecommendation: 'Hold headroom; use Smart NR/filtering',
          smartNrHeldByRxChain: false,
          smartNrRxChainLabel: 'RX chain optimized',
          maxSnrDb: 17.9,
          coherentMaxSnrDb: 16.8,
          occupiedPct: 4.2,
          coherentOccupiedPct: 2.1,
          impulsivePct: 0.4,
          peakCount: 3,
          coherentPeakCount: 2,
          coherentSubthresholdSignal: true,
        },
        frontendAudioPlayback: {
          schemaVersion: 1,
          available: true,
          ageMs: 35,
          sourceAgeMs: 48,
          sourceClockSkewMs: null,
          status: 'client-playback-healthy',
          fresh: true,
          stale: false,
          diagnosticRecommendation: 'Frontend RX playback is fresh with no reported underruns.',
          atUtc: '2026-06-15T01:00:00Z',
          sourceAtUtc: '2026-06-15T00:59:59Z',
          sourceClientId: 'frontend-audio',
          playbackState: 'playing',
          contextState: 'running',
          bufferedSamples: 14400,
          bufferedMs: 300,
          sampleRateHz: 48000,
          contextSampleRateHz: 48000,
          baseLatencyMs: 22,
          outputLatencyMs: 48,
          underrunCount: 0,
          droppedSamples: 0,
          latePushCount: 0,
          latenessVsScheduleCount: 0,
          pendingSources: 15,
          bufferTargetMs: 300,
          bufferMaxMs: 500,
          errorMessage: null,
        },
        pureSignal: {
          schemaVersion: 1,
          enabled: true,
          monitorEnabled: true,
          auto: true,
          single: false,
          autoAttenuate: true,
          feedbackSource: 'External',
          externalFeedback: true,
          externalFeedbackPathSupported: true,
          rfBypassRequired: true,
          rfBypassSelected: true,
          feedbackLevelRaw: 151.2,
          feedbackLevelPct: 59.1,
          feedbackTargetRaw: 152.293,
          feedbackUsableMinRaw: 128,
          feedbackUsableMaxRaw: 181,
          feedbackCenteredMinRaw: 138,
          feedbackCenteredMaxRaw: 176,
          txFeedbackAttenuationDb: 4,
          txFeedbackAttenuationDbMin: 0,
          hwPeak: 0.6121,
          hwPeakDefault: 0.6121,
          calState: 8,
          correcting: true,
          calibrationStalled: false,
          healthStatus: 'centered-correcting',
          manualReference: 'ANAN G2 manual: external PureSignal feedback should enter RF Bypass.',
          diagnosticRecommendation: 'External RF Bypass feedback is centered and correcting.',
        },
        g2Sensors: {
          schemaVersion: 1,
          activeProtocol: 'P2',
          connectedBoard: 'OrionMkII',
          effectiveBoard: 'OrionMkII',
          orionMkIIVariant: 'G2',
          g2Class: true,
          p2Attached: true,
          p2Packets: 42,
          p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
          status: 'manual-sensors-unmapped',
          mappedSensors: [
            {
              id: 'pa-forward-power',
              label: 'PA forward power ADC',
              telemetryPath: 'p2.fwdAdc',
              source: 'Thetis P2 hi-priority word 0x0A',
              rawValue: 91,
              status: 'mapped-live',
              notes: 'Feeds PA watts.',
            },
          ],
          unmappedManualSensors: [
            {
              id: 'pa-current',
              label: 'PA current',
              manualEvidence: 'biascheck reads PA Current',
              currentTelemetryStatus: 'p2-g2-pa-current-unmapped',
              requiredCapture: 'Capture TUN markers before scaling amps.',
              safetyClass: 'tx-monitoring-only',
            },
          ],
          candidateWords: [
            {
              offset: 28,
              hexOffset: '0x1C',
              known: null,
              last: 58752,
              min: 0,
              max: 65280,
              changeCount: 37,
              status: 'unknown-variable',
              mappingHint: 'Create before/after markers.',
            },
          ],
          manualReference: 'G2 manual documents current, voltage, temperature sensors.',
          diagnosticRecommendation: 'Use Mapping Capture before trusting current, fan, or thermal automation.',
          generatedUtc: '2026-06-15T01:00:01Z',
        },
        g2FirmwareOptions: {
          schemaVersion: 1,
          activeProtocol: 'P2',
          connectedBoard: 'OrionMkII',
          effectiveBoard: 'OrionMkII',
          orionMkIIVariant: 'G2',
          g2Class: true,
          maxRxFrequencyMhz: 60,
          maxRxFrequencyStatus: 'wired-vfo-clamp',
          rx1AttenuatorDb: 11,
          rx1AttenuatorStatus: 'mapped-live',
          rx1AttenuatorSupported: true,
          options: [
            {
              id: 'adc-dither',
              label: 'ADC dither',
              enabled: true,
              thetisDefaultEnabled: true,
              status: 'mapped-live',
              source: 'Thetis Setup > General > ANAN-G2 Options calls NetworkIO.SetADCDither; Protocol-2 CmdRx byte 5 carries ADC0..2 dither bits',
              notes: 'Zeus persists this setting in /api/radio/g2-options and writes the CmdRx byte 5 mask.',
            },
            {
              id: 'adc-random',
              label: 'ADC randomizer',
              enabled: true,
              thetisDefaultEnabled: true,
              status: 'mapped-live',
              source: 'Thetis Setup > General > ANAN-G2 Options calls NetworkIO.SetADCRandom; Protocol-2 CmdRx byte 6 carries ADC0..2 randomizer bits',
              notes: 'Zeus persists this setting in /api/radio/g2-options and writes the CmdRx byte 6 mask.',
            },
          ],
          missingControlSurface: 'SetADCDither/SetADCRandom Protocol-2 command mapping is not implemented in Zeus yet.',
          manualReference: 'Thetis ANAN-G2 Options: Dither Enabled, Random Enabled, MaxRXFreq 60.00.',
          diagnosticRecommendation: 'MaxRXFreq parity is enforced.',
          generatedUtc: '2026-06-15T01:00:02Z',
        },
        mapping: {
          schemaVersion: 2,
          markers: [
            {
              id: 1,
              label: 'RX2 on',
              createdUtc: '2026-06-14T20:00:00Z',
              activeProtocol: 'P2',
              sincePrevious: {
                baseline: true,
                p2ChangedBytes: [],
                p2ChangedWords: [],
                p1ChangedAddresses: [],
              },
            },
          ],
        },
        hardwarePotential: {
          schemaVersion: 1,
          generatedUtc: '2026-06-15T07:20:00Z',
          connectedBoard: 'OrionMkII',
          effectiveBoard: 'OrionMkII',
          orionMkIIVariant: 'G2',
          g2Class: true,
          activeProtocol: 'P2',
          currentSampleRateHz: 1536000,
          maxRxSampleRateHz: 1536000,
          fullRxSampleRateLadderHz: [48000, 96000, 192000, 384000, 768000, 1536000],
          sampleRates: [
            {
              rateHz: 48000,
              label: '48 kHz',
              supportedByBoard: true,
              supportedByActiveProtocol: true,
              currentlySelected: false,
              status: 'available',
              notes: 'Board and active protocol can use this rate.',
            },
            {
              rateHz: 1536000,
              label: '1.536 MHz',
              supportedByBoard: true,
              supportedByActiveProtocol: true,
              currentlySelected: true,
              status: 'selected',
              notes: 'Active receive sample rate.',
            },
          ],
          items: [
            {
              id: 'rx.adc2-ground-on-tx',
              title: 'ADC2 ground-on-TX protection',
              category: 'rx-protection',
              manualCapability: 'The G2 PA topology includes ADC2/RX2 grounding during transmit.',
              currentExposure: 'Protocol-2 high-priority command path already asserts RX_GNDonTX while keyed.',
              implementationStatus: 'auto-protection-wired',
              safetyClass: 'tx-protection',
              userConfigurable: false,
              telemetryPaths: ['activeProtocol'],
              currentControls: ['Protocol2Client.SendCmdHighPriority'],
              blockers: ['No separate operator override should be exposed.'],
              nextStep: 'Keep automatic protection enabled.',
            },
            {
              id: 'rx.filter-taps-window-sizes',
              title: 'RX/TX FIR tap sizes, windows, and phase policy',
              category: 'dsp-fidelity',
              manualCapability: 'G2 can feed wide DDC spans up to 1.536 MHz.',
              currentExposure: 'Blackman-Harris baseline is wired.',
              implementationStatus: 'p-invoke-gap',
              safetyClass: 'rx-safe-tx-benchmark-required',
              userConfigurable: false,
              telemetryPaths: ['hardwarePotential.filterAndWindowAudit'],
              currentControls: ['Settings > DSP'],
              blockers: ['Native WDSP setters are not fully wrapped.'],
              nextStep: 'Benchmark all tap sizes.',
            },
          ],
          ditherRandomAudit: ['G2 manual: no explicit dither/random control found.'],
          filterAndWindowAudit: ['Candidate tap sizes: 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536.'],
          diagnosticRecommendation: 'G2-class hardware potential is visible.',
        },
        featureSurfaces: [
          {
            id: 'hardware.mapping.correlation',
            title: 'Wire-field correlation markers',
            category: 'diagnostics',
            implementationStatus: 'api-ready',
            userConfigurable: false,
            source: 'diagnostics accumulator',
            telemetryPaths: ['mapping.markers'],
            candidateControls: ['/api/radio/diagnostics/map/marker'],
            safetyClass: 'rx-safe',
            notes: 'before/after deltas',
          },
        ],
      }));
    vi.stubGlobal('fetch', fetchMock);

    const diag = await createHardwareDiagnosticsMarker('RX2 on', 'toggle RX2');

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/diagnostics/map/marker');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      label: 'RX2 on',
      notes: 'toggle RX2',
    });
    expect(diag.hardwareDiagnosticsApiVersion).toBe(1);
    expect(diag.dsp.engineKind).toBe('WDSP');
    expect(diag.dsp.txOutputSamples).toBe(4096);
    expect(diag.dsp.txMonitorRequested).toBe(true);
    expect(diag.dsp.wdspNativeLoadable).toBe(true);
    expect(diag.dsp.wdspEmnrPost2Available).toBe(true);
    expect(diag.dsp.wdspNr4SbnrAvailable).toBe(false);    expect(diag.dsp.nr4Readiness).toBe('missing-sbnr-exports');    expect(diag.dsp.requestedNrMode).toBe('Sbnr');
    expect(diag.dsp.effectiveNrMode).toBe('Off');    expect(diag.dsp.rxDsp.status).toBe('nr-capability-limited');
    expect(diag.dsp.rxDsp.agcMode).toBe('Fast');
    expect(diag.dsp.rxDsp.effectiveAgcTopDb).toBe(62);
    expect(diag.dsp.rxDsp.effectiveNbpNotchesRun).toBe(true);
    expect(diag.dsp.rxDsp.activeManualNotchCount).toBe(1);    expect(diag.dsp.rxDsp.activeFeatures).toContain('manual-notches');
    expect(diag.dsp.rxDsp.qualityReasons).toContain('nr-capability-limited');
    expect(diag.dsp.rxDsp.diagnosticRecommendation).toContain('NR2/EMNR');
    expect(diag.dsp.rxMeters.status).toBe('adc-hot');
    expect(diag.dsp.rxMeters.fresh).toBe(true);
    expect(diag.dsp.rxMeters.rxDbm).toBe(-44.6);
    expect(diag.dsp.rxMeters.adcPkDbfs).toBe(-1.8);
    expect(diag.dsp.rxMeters.adcHeadroomDb).toBe(1.8);
    expect(diag.dsp.rxMeters.agcGainDb).toBe(-9.5);
    expect(diag.dsp.rxMeters.signalUsable).toBe(true);
    expect(diag.dsp.rxMeters.diagnosticRecommendation).toContain('within 3 dB');
    expect(diag.dsp.rxDynamicRange.status).toBe('adc-headroom-limited');
    expect(diag.dsp.rxDynamicRange.tone).toBe('danger');
    expect(diag.dsp.rxDynamicRange.effectiveAttenDb).toBe(5);
    expect(diag.dsp.rxDynamicRange.overloadRisk).toBe(true);
    expect(diag.dsp.rxDynamicRange.actions[0]?.id).toBe('add-attenuation');
    expect(diag.dsp.rxDynamicRange.actions[1]?.id).toBe('disable-preamp');
    expect(diag.dsp.audio.status).toBe('fresh');
    expect(diag.dsp.audio.source).toBe('rx');
    expect(diag.dsp.audio.framesBroadcast).toBe(128);
    expect(diag.dsp.audio.rmsDbfs).toBe(-30);
    expect(diag.dsp.audio.peakDbfs).toBe(-14.9);
    expect(diag.dsp.audio.squelchOpen).toBe(true);
    expect(diag.dsp.audio.squelchMode).toBe('adaptive');
    expect(diag.dsp.audio.squelchGateSource).toBe('backend-adaptive');
    expect(diag.dsp.audio.squelchOpenKnown).toBe(true);
    expect(diag.dsp.audio.diagnosticRecommendation).toContain('fresh');
    expect(diag.dsp.listenability.status).toBe('audio-recovered');
    expect(diag.dsp.listenability.tone).toBe('ready');
    expect(diag.dsp.listenability.signalPresent).toBe(true);
    expect(diag.dsp.listenability.audioRecovered).toBe(true);
    expect(diag.dsp.listenability.blocker).toBe('none');
    expect(diag.dsp.listenability.recommendation).toContain('recovered audio agree');
    expect(diag.dsp.display.status).toBe('fresh');
    expect(diag.dsp.display.panSource).toBe('rx');
    expect(diag.dsp.display.waterfallSource).toBe('rx');
    expect(diag.dsp.display.pan.maxDb).toBe(-71.2);
    expect(diag.dsp.display.waterfall.dynamicRangeDb).toBe(54.4);
    expect(diag.dsp.filterGeometry.hardwareLimits.maxRxSampleRateHz).toBe(1536000);
    expect(diag.dsp.filterGeometry.hardwareLimits.sampleRates.find((r) => r.sampleRateHz === 1536000)?.boardSupported)
      .toBe(true);
    expect(diag.dsp.filterGeometry.operatorConfigurable).toBe(true);
    expect(diag.dsp.filterGeometry.runtimeSampleRateControl.status).toBe('wideband-control-ready');
    expect(diag.dsp.filterGeometry.runtimeSampleRateControl.maxWritableSampleRateHz).toBe(1536000);
    expect(diag.dsp.filterGeometry.runtimeSampleRateControl.widebandWritable).toBe(true);
    expect(diag.dsp.filterGeometry.runtimeSampleRateControl.requiresReconnect).toBe(false);
    expect(diag.dsp.filterGeometry.runtimeSampleRateControl.apiRoute).toBe('/api/sampleRate');
    expect(diag.dsp.filterGeometry.receiverBandwidth.status).toBe('wideband-underused');
    expect(diag.dsp.filterGeometry.receiverBandwidth.utilizationPct).toBe(12.5);
    expect(diag.dsp.filterGeometry.receiverBandwidth.activeUserDdcIndex).toBe(2);
    expect(diag.dsp.filterGeometry.receiverBandwidth.manualReceiverCapacity).toBe(10);
    expect(diag.dsp.filterGeometry.receiverBandwidth.unexposedReceiverCount).toBe(9);
    expect(diag.dsp.filterGeometry.receiverBandwidth.reservedSlots[1]?.slot).toBe(1);
    expect(diag.dsp.filterGeometry.optionCatalog.iqBufferSizes).toEqual([64, 128, 256, 512, 1024]);
    expect(diag.dsp.filterGeometry.optionCatalog.filterTapSizes[0]).toBe(64);
    expect(diag.dsp.filterGeometry.optionCatalog.filterTapSizes).toContain(16384);
    expect(diag.dsp.filterGeometry.optionCatalog.filterTapSizes).toContain(262144);
    expect(diag.dsp.filterGeometry.optionCatalog.filterWindows[1]?.label).toBe('BH-7');
    expect(diag.dsp.filterGeometry.activeRx.filterWindow).toBe('BH-7');
    expect(diag.dsp.filterGeometry.thetisMatrix[1]?.filterType).toBe('Linear Phase');
    expect(diag.dsp.filterGeometry.impulseCache.status).toBe('fftw-wisdom-only');
    expect(diag.frontendDspScene.available).toBe(true);
    expect(diag.frontendDspScene.status).toBe('fresh');
    expect(diag.frontendDspScene.fresh).toBe(true);
    expect(diag.frontendDspScene.stale).toBe(false);
    expect(diag.frontendDspScene.mode).toBe('USB');
    expect(diag.frontendDspScene.diagnosticRecommendation).toContain('fresh');
    expect(diag.frontendDspScene.signalProfile).toBe('dx');
    expect(diag.frontendDspScene.smartNrRecommendation).toBe('Hold headroom; use Smart NR/filtering');
    expect(diag.frontendDspScene.coherentPeakCount).toBe(2);
    expect(diag.frontendDspScene.coherentSubthresholdSignal).toBe(true);
    expect(diag.frontendAudioPlayback.available).toBe(true);
    expect(diag.frontendAudioPlayback.status).toBe('client-playback-healthy');
    expect(diag.frontendAudioPlayback.bufferedMs).toBe(300);
    expect(diag.frontendAudioPlayback.baseLatencyMs).toBe(22);
    expect(diag.frontendAudioPlayback.outputLatencyMs).toBe(48);
    expect(diag.frontendAudioPlayback.pendingSources).toBe(15);
    expect(diag.pureSignal.feedbackSource).toBe('external');
    expect(diag.pureSignal.rfBypassSelected).toBe(true);
    expect(diag.pureSignal.feedbackLevelRaw).toBe(151.2);
    expect(diag.pureSignal.healthStatus).toBe('centered-correcting');
    expect(diag.pureSignal.diagnosticRecommendation).toContain('centered');
    expect(diag.g2Sensors.status).toBe('manual-sensors-unmapped');
    expect(diag.g2Sensors.mappedSensors[0]?.telemetryPath).toBe('p2.fwdAdc');
    expect(diag.g2Sensors.unmappedManualSensors[0]?.currentTelemetryStatus).toBe('p2-g2-pa-current-unmapped');
    expect(diag.g2Sensors.candidateWords[0]?.hexOffset).toBe('0x1C');
    expect(diag.g2Sensors.diagnosticRecommendation).toContain('Mapping Capture');
    expect(diag.g2FirmwareOptions.options[0]?.id).toBe('adc-dither');
    expect(diag.g2FirmwareOptions.maxRxFrequencyMhz).toBe(60);
    expect(diag.g2FirmwareOptions.rx1AttenuatorDb).toBe(11);
    expect(diag.g2FirmwareOptions.rx1AttenuatorSupported).toBe(true);
    expect(diag.g2FirmwareOptions.options[1]?.status).toBe('mapped-live');
    expect(diag.mapping.schemaVersion).toBe(2);
    expect(diag.mapping.markers[0]?.label).toBe('RX2 on');
    expect(diag.hardwarePotential.g2Class).toBe(true);
    expect(diag.hardwarePotential.sampleRates.map((rate) => rate.rateHz)).toEqual([48000, 1536000]);
    expect(diag.hardwarePotential.fullRxSampleRateLadderHz).toContain(1536000);
    expect(diag.hardwarePotential.items[0]?.id).toBe('rx.adc2-ground-on-tx');
    expect(diag.hardwarePotential.items[0]?.implementationStatus).toBe('auto-protection-wired');
    expect(diag.hardwarePotential.ditherRandomAudit[0]).toContain('dither/random');
    expect(diag.hardwarePotential.filterAndWindowAudit[0]).toContain('65536');
    expect(diag.featureSurfaces[0]?.id).toBe('hardware.mapping.correlation');
  });

  it('keeps frontend DSP scene mode null when diagnostics are unavailable', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      hardwareDiagnosticsApiVersion: 1,
      connectionStatus: 'Connected',
      mode: 'USB',
      frontendDspScene: {
        schemaVersion: 1,
        available: false,
        ageMs: null,
        status: 'missing',
        fresh: false,
        stale: false,
        diagnosticRecommendation: 'No frontend DSP scene has been published yet.',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const diag = await fetchHardwareDiagnostics();

    expect(diag.frontendDspScene.available).toBe(false);
    expect(diag.frontendDspScene.status).toBe('missing');
    expect(diag.frontendDspScene.mode).toBeNull();
    expect(diag.mode).toBe('USB');
  });

  it('publishFrontendDspSceneDiagnostics posts frontend scene evidence', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      ageMs: 12,
      status: 'fresh',
      fresh: true,
      stale: false,
      diagnosticRecommendation: 'Frontend DSP scene telemetry is fresh and ready for remote diagnostics.',
      atUtc: '2026-06-15T01:00:00Z',
      sourceAtUtc: '2026-06-15T00:59:58Z',
      sourceAgeMs: 2012,
      sourceClockSkewMs: 6020,
      sourceClientId: 'frontend-test',
      mode: 'USB',
      signalProfile: 'dx',
      smartNrProfile: 'NR4',
      smartNrRxChainLabel: 'ADC headroom limited',
      smartNrRxChainRecommendation: 'Add 3-6 dB attenuation',
      smartNrRxChainTone: 'protect',
      smartNrRxChainScore: 62,
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
      adjacentNoiseUsable: true,
      adjacentNoiseBins: 84,
      adjacentNoiseFloorDb: -111.2,
      adjacentNoiseRejectedPct: 4.8,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const scene = await publishFrontendDspSceneDiagnostics({
      sourceAtUtc: '2026-06-15T00:59:58Z',
      sourceClientId: 'frontend-test',
      mode: 'USB',
      signalProfile: 'dx',
      smartNrProfile: 'NR4',
      smartNrRxChainLabel: 'ADC headroom limited',
      smartNrRxChainRecommendation: 'Add 3-6 dB attenuation',
      smartNrRxChainTone: 'protect',
      smartNrRxChainScore: 62,
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
      adjacentNoiseUsable: true,
      adjacentNoiseBins: 84,
      adjacentNoiseFloorDb: -111.2,
      adjacentNoiseRejectedPct: 4.8,
    });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/diagnostics/dsp-scene');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      sourceAtUtc: '2026-06-15T00:59:58Z',
      sourceClientId: 'frontend-test',
      mode: 'USB',
      signalProfile: 'dx',
      smartNrProfile: 'NR4',
      smartNrRxChainLabel: 'ADC headroom limited',
      smartNrRxChainRecommendation: 'Add 3-6 dB attenuation',
      smartNrRxChainTone: 'protect',
      smartNrRxChainScore: 62,
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
      adjacentNoiseUsable: true,
      adjacentNoiseBins: 84,
      adjacentNoiseFloorDb: -111.2,
      adjacentNoiseRejectedPct: 4.8,
    });
    expect(scene.available).toBe(true);
    expect(scene.status).toBe('fresh');
    expect(scene.fresh).toBe(true);
    expect(scene.sourceAtUtc).toBe('2026-06-15T00:59:58Z');
    expect(scene.sourceAgeMs).toBe(2012);
    expect(scene.sourceClockSkewMs).toBe(6020);
    expect(scene.signalProfile).toBe('dx');
    expect(scene.smartNrProfile).toBe('NR4');
    expect(scene.smartNrRxChainRecommendation).toBe('Add 3-6 dB attenuation');
    expect(scene.smartNrRxChainTone).toBe('protect');
    expect(scene.smartNrRxChainScore).toBe(62);
    expect(scene.coherentSubthresholdSignal).toBe(true);
    expect(scene.adjacentNoiseUsable).toBe(true);
    expect(scene.adjacentNoiseBins).toBe(84);
    expect(scene.adjacentNoiseFloorDb).toBe(-111.2);
    expect(scene.adjacentNoiseRejectedPct).toBe(4.8);
  });

  it('fetchFrontendDspSceneDiagnostics reads the latest frontend scene snapshot', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      ageMs: 32,
      status: 'fresh',
      fresh: true,
      stale: false,
      diagnosticRecommendation: 'Frontend DSP scene telemetry is fresh.',
      atUtc: '2026-06-15T01:00:02Z',
      sourceAtUtc: '2026-06-15T01:00:01Z',
      sourceAgeMs: 1032,
      sourceClockSkewMs: null,
      sourceClientId: 'frontend-live',
      mode: 'LSB',
      signalProfile: 'weak-sparse',
      signalReason: 'single coherent ridge',
      smartNrProfile: 'NR2',
      smartNrRecommendation: 'Keep RX headroom and use gentle NR2',
      smartNrRxChainLabel: 'AGC stressed',
      smartNrRxChainRecommendation: 'Auto AGC reducing AGC-T under ADC pressure',
      smartNrRxChainTone: 'optimize',
      smartNrRxChainScore: 68,
      maxSnrDb: 5.6,
      coherentMaxSnrDb: 5.3,
      peakCount: 1,
      coherentPeakCount: 1,
      coherentSubthresholdSignal: true,
      topPeaks: [
        {
          frequencyHz: 14269200,
          offsetHz: 2200,
          snrDb: 24.1,
          dbfs: -86.2,
          confidence: 0.7,
          coherent: true,
        },
      ],
      adjacentNoiseUsable: true,
      adjacentNoiseBins: 96,
      adjacentNoiseP50Db: -109.4,
      adjacentNoiseRejectedPct: 3.1,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const scene = await fetchFrontendDspSceneDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/diagnostics/dsp-scene');
    expect(init?.method).toBeUndefined();
    expect(scene.available).toBe(true);
    expect(scene.status).toBe('fresh');
    expect(scene.mode).toBe('LSB');
    expect(scene.sourceClientId).toBe('frontend-live');
    expect(scene.signalProfile).toBe('weak-sparse');
    expect(scene.smartNrProfile).toBe('NR2');
    expect(scene.smartNrRxChainLabel).toBe('AGC stressed');
    expect(scene.smartNrRxChainRecommendation).toBe('Auto AGC reducing AGC-T under ADC pressure');
    expect(scene.smartNrRxChainTone).toBe('optimize');
    expect(scene.smartNrRxChainScore).toBe(68);
    expect(scene.coherentSubthresholdSignal).toBe(true);
    expect(scene.topPeaks).toEqual([
      {
        frequencyHz: 14269200,
        offsetHz: 2200,
        snrDb: 24.1,
        dbfs: -86.2,
        confidence: 0.7,
        coherent: true,
      },
    ]);
    expect(scene.adjacentNoiseUsable).toBe(true);
    expect(scene.adjacentNoiseBins).toBe(96);
    expect(scene.adjacentNoiseP50Db).toBe(-109.4);
    expect(scene.adjacentNoiseRejectedPct).toBe(3.1);
  });

  it('publishFrontendAudioPlaybackDiagnostics posts browser RX playback health', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      ageMs: 24,
      sourceAgeMs: 36,
      sourceClockSkewMs: null,
      status: 'client-main-thread-late',
      fresh: true,
      stale: false,
      diagnosticRecommendation: 'Frontend RX playback underruns include late websocket/main-thread delivery.',
      atUtc: '2026-06-15T13:00:02Z',
      sourceAtUtc: '2026-06-15T13:00:01Z',
      sourceClientId: 'frontend-audio',
      playbackState: 'playing',
      contextState: 'running',
      bufferedSamples: 7200,
      bufferedMs: 150,
      sampleRateHz: 48000,
      contextSampleRateHz: 48000,
      baseLatencyMs: 22,
      outputLatencyMs: 48,
      underrunCount: 3,
      droppedSamples: 0,
      latePushCount: 2,
      latenessVsScheduleCount: 1,
      pendingSources: 7,
      bufferTargetMs: 300,
      bufferMaxMs: 500,
      errorMessage: null,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const playback = await publishFrontendAudioPlaybackDiagnostics({
      sourceAtUtc: '2026-06-15T13:00:01Z',
      sourceClientId: 'frontend-audio',
      playbackState: 'playing',
      contextState: 'running',
      bufferedSamples: 7200,
      bufferedMs: 150,
      sampleRateHz: 48000,
      contextSampleRateHz: 48000,
      baseLatencyMs: 22,
      outputLatencyMs: 48,
      underrunCount: 3,
      droppedSamples: 0,
      latePushCount: 2,
      latenessVsScheduleCount: 1,
      pendingSources: 7,
      bufferTargetMs: 300,
      bufferMaxMs: 500,
      errorMessage: null,
    });

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/diagnostics/audio-playback');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      sourceAtUtc: '2026-06-15T13:00:01Z',
      sourceClientId: 'frontend-audio',
      playbackState: 'playing',
      contextState: 'running',
      bufferedSamples: 7200,
      bufferedMs: 150,
      sampleRateHz: 48000,
      contextSampleRateHz: 48000,
      baseLatencyMs: 22,
      outputLatencyMs: 48,
      underrunCount: 3,
      droppedSamples: 0,
      latePushCount: 2,
      latenessVsScheduleCount: 1,
      pendingSources: 7,
      bufferTargetMs: 300,
      bufferMaxMs: 500,
      errorMessage: null,
    });
    expect(playback.available).toBe(true);
    expect(playback.status).toBe('client-main-thread-late');
    expect(playback.playbackState).toBe('playing');
    expect(playback.bufferedMs).toBe(150);
    expect(playback.underrunCount).toBe(3);
    expect(playback.latePushCount).toBe(2);
    expect(playback.pendingSources).toBe(7);
  });

  it('fetchFrontendAudioPlaybackDiagnostics reads the latest browser RX playback snapshot', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      ageMs: 12,
      sourceAgeMs: 15,
      sourceClockSkewMs: null,
      status: 'client-playback-healthy',
      fresh: true,
      stale: false,
      diagnosticRecommendation: 'Frontend RX playback is fresh with no reported underruns.',
      atUtc: '2026-06-15T13:00:02Z',
      sourceAtUtc: '2026-06-15T13:00:01Z',
      sourceClientId: 'frontend-audio',
      playbackState: 'playing',
      contextState: 'running',
      bufferedSamples: 14400,
      bufferedMs: 300,
      sampleRateHz: 48000,
      contextSampleRateHz: 48000,
      baseLatencyMs: 22,
      outputLatencyMs: null,
      underrunCount: 0,
      droppedSamples: 0,
      latePushCount: 0,
      latenessVsScheduleCount: 0,
      pendingSources: 15,
      bufferTargetMs: 300,
      bufferMaxMs: 500,
      errorMessage: null,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const playback = await fetchFrontendAudioPlaybackDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/diagnostics/audio-playback');
    expect(init?.method).toBeUndefined();
    expect(playback.status).toBe('client-playback-healthy');
    expect(playback.sourceClientId).toBe('frontend-audio');
    expect(playback.contextState).toBe('running');
    expect(playback.bufferedSamples).toBe(14400);
    expect(playback.outputLatencyMs).toBeNull();
  });

  it('fetchSmartNrCondition reads live Smart NR condition evidence', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      status: 'fresh',
      fresh: true,
      stale: false,
      ageMs: 18,
      atUtc: '2026-06-15T01:00:00Z',
      sourceAtUtc: '2026-06-15T00:59:59Z',
      sourceAgeMs: 1018,
      sourceClockSkewMs: null,
      sourceClientId: 'frontend-test',
      mode: 'USB',
      profile: 'NR2',
      reason: 'weak sparse signal',
      recommendation: 'Keep RX headroom and use gentle NR2',
      heldByRxChain: true,
      rxChainLabel: 'ADC headroom limited',
      rxChainRecommendation: 'Add 3-6 dB attenuation',
      rxChainTone: 'protect',
      rxChainScore: 62,
      maxSnrDb: 7.1,
      coherentMaxSnrDb: 6.8,
      occupiedPct: 1.2,
      coherentOccupiedPct: 0.8,
      impulsivePct: 0.1,
      peakCount: 1,
      coherentPeakCount: 1,
      coherentSubthresholdSignal: true,
      wdspActive: true,
      wdspNativeLoadable: true,
      wdspEmnrPost2Available: true,
      wdspNr4SbnrAvailable: false,
      nr4Readiness: 'missing-sbnr-exports',
      requestedNrMode: 'Sbnr',
      effectiveNrMode: 'Off',
      expectedNrMode: 'Emnr',
      runtimeAligned: false,
      runtimeAlignmentStatus: 'mismatched',
      runtimeAlignmentRecommendation: 'Smart NR profile NR2 maps to WDSP Emnr, but the backend is requested=Sbnr effective=Off.',
      rxChain: {
        schemaVersion: 2,
        source: 'backend-radio-state',
        filterLowHz: 300,
        filterHighHz: 2600,
        filterWidthHz: 2300,
        filterPresetName: 'WEAK-RX',
        autoAgcEnabled: true,
        agcMode: 'Fast',
        agcTopDb: 83,
        agcOffsetDb: -31,
        effectiveAgcTopDb: 52,
        autoAttEnabled: true,
        adcProtectionEnabled: true,
        attenDb: 0,
        attOffsetDb: 2,
        effectiveAttenDb: 2,
        adcOverloadWarning: true,
        adcOverloadLevel: 4,
        lastOverloadBits: 1,
        adc0MaxMagnitude: 44000,
        adc1MaxMagnitude: null,
        adc0MaxMagnitudeAtOverload: 50000,
        adc1MaxMagnitudeAtOverload: 0,
        lastAdcTelemetryUtc: '2026-06-15T01:00:00Z',
        squelchEnabled: true,
        squelchAdaptive: true,
        squelchLevel: 18,
        preampOn: false,
      },
      diagnosticRecommendation: 'Smart NR is currently constrained by RX-chain health.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const condition = await fetchSmartNrCondition();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/dsp/nr-condition');
    expect(init?.method).toBeUndefined();
    expect(condition.available).toBe(true);
    expect(condition.status).toBe('fresh');
    expect(condition.mode).toBe('USB');
    expect(condition.profile).toBe('NR2');
    expect(condition.heldByRxChain).toBe(true);
    expect(condition.rxChainLabel).toBe('ADC headroom limited');
    expect(condition.rxChainRecommendation).toBe('Add 3-6 dB attenuation');
    expect(condition.rxChainTone).toBe('protect');
    expect(condition.rxChainScore).toBe(62);
    expect(condition.maxSnrDb).toBe(7.1);
    expect(condition.coherentSubthresholdSignal).toBe(true);
    expect(condition.wdspNr4SbnrAvailable).toBe(false);    expect(condition.nr4Readiness).toBe('missing-sbnr-exports');    expect(condition.requestedNrMode).toBe('Sbnr');
    expect(condition.effectiveNrMode).toBe('Off');    expect(condition.expectedNrMode).toBe('Emnr');
    expect(condition.runtimeAligned).toBe(false);
    expect(condition.runtimeAlignmentStatus).toBe('mismatched');
    expect(condition.runtimeAlignmentRecommendation).toContain('maps to WDSP Emnr');
    expect(condition.rxChain.source).toBe('backend-radio-state');
    expect(condition.rxChain.filterLowHz).toBe(300);
    expect(condition.rxChain.filterHighHz).toBe(2600);
    expect(condition.rxChain.filterWidthHz).toBe(2300);
    expect(condition.rxChain.filterPresetName).toBe('WEAK-RX');
    expect(condition.rxChain.autoAgcEnabled).toBe(true);
    expect(condition.rxChain.agcMode).toBe('Fast');
    expect(condition.rxChain.effectiveAgcTopDb).toBe(52);
    expect(condition.rxChain.effectiveAttenDb).toBe(2);
    expect(condition.rxChain.adcOverloadWarning).toBe(true);
    expect(condition.rxChain.adc0MaxMagnitude).toBe(44000);
    expect(condition.rxChain.adc1MaxMagnitude).toBeNull();
    expect(condition.rxChain.squelchEnabled).toBe(true);
  });

  it('fetchDspLiveDiagnostics reads modernization readiness and benchmark gates', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      generatedUtc: '2026-06-15T18:22:00Z',
      status: 'replacement-preflight-required',
      qualityTone: 'protect',
      readinessScore: 70,
      readyForLiveBenchmark: false,
      readyForExternalEngineBakeoff: false,
      externalEngineBakeoffStatus: 'external-engine-bakeoff-preflight-required',
      externalEngineBakeoffConstraints: ['replacement-preflight-required'],
      rolloutGate: 'opt-in-only-until-benchmark-and-g2-on-air-acceptance',
      wdspActive: true,
      wdspNativeLoadable: true,
      wdspEmnrPost2Available: true,
      wdspNr4SbnrAvailable: true,
      nr4Readiness: 'available',
      frontendSceneAvailable: true,
      frontendSceneStatus: 'fresh',
      frontendSceneFresh: true,
      frontendSceneStale: false,
      frontendSceneAgeMs: 42,
      smartNrProfile: 'NR4',
      expectedNrMode: 'Sbnr',
      runtimeAligned: true,
      runtimeAlignmentStatus: 'aligned',
      requestedNrMode: 'Sbnr',
      effectiveNrMode: 'Sbnr',
      heldByRxChain: false,
      rxChainScore: 94,
      rxChainTone: 'neutral',
      rxChainLabel: 'RX chain optimized',
      rxChainFilterLowHz: 300,
      rxChainFilterHighHz: 2600,
      rxChainFilterWidthHz: 2300,
      rxChainFilterPresetName: 'WEAK-RX',
      evidence: ['wdsp-active'],
      constraints: ['replacement-preflight-required'],
      recommendedActions: ['Capture replacement-candidate evidence before promotion.'],
      candidateTools: ['external-post-demod-bakeoff:rnnoise'],
      benchmarkPlanEndpoint: '/api/dsp/benchmark-plan',
      benchmarkScenarioCount: 12,
      nextBenchmarkScenarios: ['weak-cw-carrier', 'agc-level-step'],
      benchmarkAcceptanceGates: ['No audible AGC or NR pumping.'],
      externalEngineCandidates: [
        {
          schemaVersion: 1,
          id: 'rnnoise',
          name: 'RNNoise',
          family: 'neural-speech-denoiser',
          integrationPoint: 'post-demod-rx-audio-speech-only',
          defaultState: 'off',
          rolloutPolicy: 'candidate-only-opt-in-bakeoff',
          license: 'BSD-3-Clause',
          packagingStatus: 'native-c-library-not-vendored',
          runtimeRisk: 'medium',
          latencyRisk: 'low-medium',
          radioSafetyRisk: 'medium: speech-trained model may damage weak CW',
          strengths: ['Small C runtime'],
          requiredBenchmarks: ['ssb-like-speech', 'weak-cw-carrier'],
          requiredEvidence: ['Must preserve weak carrier/CW fixtures.'],
          blockers: ['No bundled native package or model artifact.'],
          referenceUrls: ['https://github.com/xiph/rnnoise'],
        },
      ],
      diagnosticRecommendation: 'Capture NR-off/current-Zeus plus opt-in replacement-candidate evidence before promotion.',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const diag = await fetchDspLiveDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/dsp/live-diagnostics');
    expect(init?.method).toBeUndefined();
    expect(diag.status).toBe('replacement-preflight-required');
    expect(diag.readinessScore).toBe(70);
    expect(diag.readyForLiveBenchmark).toBe(false);
    expect(diag.readyForExternalEngineBakeoff).toBe(false);
    expect(diag.externalEngineBakeoffStatus).toBe('external-engine-bakeoff-preflight-required');
    expect(diag.externalEngineBakeoffConstraints).toContain('replacement-preflight-required');
    expect(diag.frontendSceneFresh).toBe(true);
    expect(diag.runtimeAligned).toBe(true);
    expect(diag.rxChainFilterLowHz).toBe(300);
    expect(diag.rxChainFilterHighHz).toBe(2600);
    expect(diag.rxChainFilterWidthHz).toBe(2300);
    expect(diag.rxChainFilterPresetName).toBe('WEAK-RX');    expect(diag.benchmarkPlanEndpoint).toBe('/api/dsp/benchmark-plan');
    expect(diag.benchmarkScenarioCount).toBe(12);
    expect(diag.nextBenchmarkScenarios).toEqual(['weak-cw-carrier', 'agc-level-step']);
    expect(diag.benchmarkAcceptanceGates[0]).toContain('pumping');
    expect(diag.externalEngineCandidates[0]?.id).toBe('rnnoise');
    expect(diag.externalEngineCandidates[0]?.defaultState).toBe('off');
    expect(diag.externalEngineCandidates[0]?.requiredBenchmarks).toContain('weak-cw-carrier');
    expect(diag.externalEngineCandidates[0]?.blockers[0]).toContain('No bundled native package');
  });

  it('fetchTxDiagnostics reads P2 DUC egress counters', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      generatedUtc: '2026-06-15T13:00:01Z',
      iqSourceType: 'Zeus.Protocol1.TxIqRing',
      iqSourceIsRing: true,
      ring: {
        totalWritten: 960,
        totalRead: 0,
        count: 0,
        dropped: 0,
        capacity: 16384,
        recentMag: 0.125,
      },
      ingest: {
        totalMicSamples: 480,
        totalTxBlocks: 1,
        droppedFrames: 0,
      },
      protocol2: {
        inputComplexSamples: 240,
        packetsQueued: 1,
        packetsSent: 1,
        queuedPackets: 0,
        queueWriteFailures: 0,
        sendFailures: 0,
        resetDrainedPackets: 0,
        scratchComplexSamples: 0,
        nextSequence: 1,
        lastPacketsPerSecond: 800,
        lastFifoModelSamples: 1200,
        lastRateTimestampUtc: '2026-06-15T13:00:00Z',
        senderRunning: true,
      },
      micUplink: {
        schemaVersion: 1,
        status: 'live',
        subscriberAttached: true,
        clientCount: 1,
        expectedFrameSamples: 960,
        expectedFrameBytes: 3840,
        totalFrames: 24,
        totalSamples: 23040,
        totalBytes: 92160,
        lastFrameBytes: 3840,
        lastFrameSamples: 960,
        lastFrameAgeMs: 80,
        lastFrameUtc: '2026-06-15T13:00:00.920Z',
        invalidFrames: 1,
        oversizeMessages: 0,
        unknownFrames: 2,
        diagnosticRecommendation: 'Mic PCM uplink is live.',
      },
      audioPath: {
        schemaVersion: 1,
        status: 'tx-audio-flowing',
        hostTxActive: true,
        p2Attached: true,
        p2DucLive: true,
        p2WaitingForTx: false,
        p2LastActivityAgeMs: 1000,
        p2InputComplexSamples: 240,
        p2PacketsSent: 1,
        p2QueuedPackets: 0,
        requiresMicUplink: true,
        micUplinkStatus: 'live',
        micUplinkLastFrameAgeMs: 80,
        micUplinkFrames: 24,
        micUplinkSamples: 23040,
        micUplinkInvalidFrames: 1,
        micUplinkOversizeMessages: 0,
        totalMicSamples: 480,
        totalTxBlocks: 1,
        droppedFrames: 0,
        ringTotalWritten: 960,
        ringTotalRead: 0,
        ringCount: 0,
        ringCapacity: 16384,
        ringFillPct: 0,
        ringDropped: 0,
        ringDropRatioPct: 0,
        ringRecentMag: 0.125,
        diagnosticRecommendation: 'TX audio is reaching the active DUC path.',
      },
      stage: {
        schemaVersion: 1,
        source: 'wdsp-txa-meter-ring',
        status: 'active',
        hostTxActive: true,
        micPkDbfs: -10.2,
        micAvDbfs: -21.4,
        eqPkDbfs: -9.8,
        eqAvDbfs: -20.5,
        lvlrPkDbfs: -8.5,
        lvlrAvDbfs: -18.4,
        lvlrGrDb: 2.1,
        cfcPkDbfs: -7.7,
        cfcAvDbfs: -17.2,
        cfcGrDb: 1.4,
        compPkDbfs: -6.8,
        compAvDbfs: -16.9,
        alcPkDbfs: -4.2,
        alcAvDbfs: -15.1,
        alcGrDb: 3.5,
        outPkDbfs: -1.8,
        outAvDbfs: -12.0,
        outputHeadroomDb: 1.8,
        outputCrestFactorDb: 10.2,
        densityStatus: 'density-optimized',
        densityTone: 'ready',
        densityRecommendation: 'TX output density and headroom are in the target window.',
        diagnosticRecommendation: 'WDSP TXA stage meters are live.',
      },
      egress: {
        schemaVersion: 1,
        generatedUtc: '2026-06-15T13:00:01Z',
        activeTransport: 'P2',
        healthStatus: 'p2-live',
        p2Attached: true,
        p2Live: true,
        p2LastActivityAgeMs: 1000,
        p1RingDropRatioPct: 0,
        hostMoxOn: true,
        hostTunOn: false,
        hostTwoToneOn: false,
        hostTxActive: true,
        hardwarePtt: false,
        forwardWatts: 15.2,
        rfDetected: true,
        rfEvidenceStatus: 'rf-active',
        qualityScore: 100,
        qualityTone: 'ready',
        p2PacketRateStatus: 'fresh',
        p2LastPacketsPerSecond: 800,
        p2FifoModelSamples: 1200,
        p2QueuedPackets: 0,
        p2TransportFailures: 0,
        qualityReasons: ['p2-rate-fresh', 'host-tx-active', 'rf-forward-power-present'],
        txDutyProfile: 'continuous-duty',
        continuousDutyRecommendedMaxWatts: 30,
        continuousDutyLimitExceeded: true,
        continuousDutyManualReference: 'ANAN-G2 manual: Data/AM/FM continuous-duty guidance.',
        diagnosticRecommendation: 'P2 DUC egress and RF forward-power evidence are live.',
      },
      txPlugins: {
        masterBypassed: false,
        bypassedForRemoteTx: false,
      },
      vstEngine: {
        active: false,
        degradedBlocks: 2,
      },
      rxVstEngine: {
        active: true,
        available: true,
        activePlugins: 1,
        degradedBlocks: 4,
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const diag = await fetchTxDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/diag');
    expect(init?.method).toBeUndefined();
    expect(diag.iqSourceIsRing).toBe(true);
    expect(diag.ring.totalWritten).toBe(960);
    expect(diag.ingest.totalTxBlocks).toBe(1);
    expect(diag.protocol2?.packetsSent).toBe(1);
    expect(diag.protocol2?.lastPacketsPerSecond).toBe(800);
    expect(diag.protocol2?.senderRunning).toBe(true);
    expect(diag.audioPath.status).toBe('tx-audio-flowing');
    expect(diag.audioPath.p2DucLive).toBe(true);
    expect(diag.audioPath.p2WaitingForTx).toBe(false);
    expect(diag.audioPath.p2LastActivityAgeMs).toBe(1000);
    expect(diag.audioPath.p2InputComplexSamples).toBe(240);
    expect(diag.audioPath.requiresMicUplink).toBe(true);
    expect(diag.audioPath.micUplinkStatus).toBe('live');
    expect(diag.audioPath.micUplinkFrames).toBe(24);
    expect(diag.audioPath.micUplinkInvalidFrames).toBe(1);
    expect(diag.audioPath.ringCapacity).toBe(16384);
    expect(diag.audioPath.ringRecentMag).toBe(0.125);
    expect(diag.audioPath.diagnosticRecommendation).toBe('TX audio is reaching the active DUC path.');
    expect(diag.micUplink.status).toBe('live');
    expect(diag.micUplink.subscriberAttached).toBe(true);
    expect(diag.micUplink.expectedFrameSamples).toBe(960);
    expect(diag.micUplink.totalFrames).toBe(24);
    expect(diag.micUplink.lastFrameAgeMs).toBe(80);
    expect(diag.micUplink.invalidFrames).toBe(1);
    expect(diag.micUplink.unknownFrames).toBe(2);
    expect(diag.stage.status).toBe('active');
    expect(diag.stage.source).toBe('wdsp-txa-meter-ring');
    expect(diag.stage.micPkDbfs).toBe(-10.2);
    expect(diag.stage.lvlrGrDb).toBe(2.1);
    expect(diag.stage.cfcGrDb).toBe(1.4);
    expect(diag.stage.alcGrDb).toBe(3.5);
    expect(diag.stage.outPkDbfs).toBe(-1.8);
    expect(diag.stage.outputHeadroomDb).toBe(1.8);
    expect(diag.stage.outputCrestFactorDb).toBe(10.2);
    expect(diag.stage.densityStatus).toBe('density-optimized');
    expect(diag.stage.densityTone).toBe('ready');
    expect(diag.stage.densityRecommendation).toContain('target window');
    expect(diag.stage.diagnosticRecommendation).toBe('WDSP TXA stage meters are live.');
    expect(diag.egress.healthStatus).toBe('p2-live');
    expect(diag.egress.p2Live).toBe(true);
    expect(diag.egress.p2LastActivityAgeMs).toBe(1000);
    expect(diag.egress.hostTxActive).toBe(true);
    expect(diag.egress.hardwarePtt).toBe(false);
    expect(diag.egress.forwardWatts).toBe(15.2);
    expect(diag.egress.rfDetected).toBe(true);
    expect(diag.egress.rfEvidenceStatus).toBe('rf-active');
    expect(diag.egress.qualityScore).toBe(100);
    expect(diag.egress.qualityTone).toBe('ready');
    expect(diag.egress.p2PacketRateStatus).toBe('fresh');
    expect(diag.egress.p2LastPacketsPerSecond).toBe(800);
    expect(diag.egress.p2FifoModelSamples).toBe(1200);
    expect(diag.egress.p2QueuedPackets).toBe(0);
    expect(diag.egress.p2TransportFailures).toBe(0);
    expect(diag.egress.qualityReasons).toContain('rf-forward-power-present');
    expect(diag.egress.txDutyProfile).toBe('continuous-duty');
    expect(diag.egress.continuousDutyRecommendedMaxWatts).toBe(30);
    expect(diag.egress.continuousDutyLimitExceeded).toBe(true);
    expect(diag.egress.continuousDutyManualReference).toContain('ANAN-G2 manual');
    expect(diag.egress.diagnosticRecommendation).toBe('P2 DUC egress and RF forward-power evidence are live.');
    expect(diag.txPlugins?.masterBypassed).toBe(false);
    expect(diag.vstEngine?.degradedBlocks).toBe(2);
    expect(diag.rxVstEngine?.available).toBe(true);
    expect(diag.rxVstEngine?.active).toBe(true);
    expect(diag.rxVstEngine?.activePlugins).toBe(1);
    expect(diag.rxVstEngine?.degradedBlocks).toBe(4);
  });

  it('fetchExternalPttStatus reads read-only external PTT ownership state', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      available: true,
      protocol: 'P1',
      hardwarePtt: true,
      cwKeyDown: false,
      ownedMox: true,
      hangTimeMs: 250,
      moxOn: true,
      tunOn: false,
      twoToneOn: false,
      moxOwner: 'Hardware',
      cwMode: true,
      sidetoneAvailable: true,
      diagnosticRecommendation: 'External PTT owns MOX through the hardware source path.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchExternalPttStatus();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/external-ptt');
    expect(init?.method).toBeUndefined();
    expect(status.available).toBe(true);
    expect(status.protocol).toBe('P1');
    expect(status.hardwarePtt).toBe(true);
    expect(status.ownedMox).toBe(true);
    expect(status.hangTimeMs).toBe(250);
    expect(status.moxOwner).toBe('Hardware');
    expect(status.cwMode).toBe(true);
  });

  it('fetchHardwareKeyingStatus reads decoded P1 and P2 key telemetry', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      p1Packets: 12,
      p1LastUpdatedUtc: '2026-06-15T00:59:50Z',
      p1HardwarePtt: false,
      p1CwKeyDown: null,
      p2Packets: 45,
      p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
      p2PttIn: true,
      p2DotIn: false,
      p2DashIn: true,
      p2SidetoneActive: true,
      externalPtt: {
        schemaVersion: 1,
        available: false,
        protocol: 'none',
        hardwarePtt: null,
        cwKeyDown: null,
        ownedMox: false,
        hangTimeMs: 250,
        moxOn: false,
        tunOn: false,
        twoToneOn: false,
        moxOwner: null,
        cwMode: false,
        sidetoneAvailable: false,
        diagnosticRecommendation: 'External PTT takeover is idle.',
        generatedUtc: '2026-06-15T01:00:01Z',
      },
      diagnosticRecommendation: 'Protocol-2 PTT, dot, dash, and sidetone telemetry are live.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchHardwareKeyingStatus();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/cw/hardware-keying');
    expect(init?.method).toBeUndefined();
    expect(status.activeProtocol).toBe('P2');
    expect(status.p1Packets).toBe(12);
    expect(status.p2Packets).toBe(45);
    expect(status.p2PttIn).toBe(true);
    expect(status.p2DashIn).toBe(true);
    expect(status.p2SidetoneActive).toBe(true);
    expect(status.externalPtt.available).toBe(false);
    expect(status.externalPtt.hangTimeMs).toBe(250);
  });

  it('fetchRadioPowerCalibration reads calibrated PA power evidence', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P1',
      connectedBoard: 'OrionMkII',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      calibrationBoard: 'OrionMkII',
      bridgeVolt: 0.12,
      refVoltage: 5,
      adcCalOffset: 32,
      calibrationMaxWatts: 100,
      calibrationFallbackApplied: false,
      capabilityMaxPowerWatts: 120,
      p1: {
        packets: 24,
        lastUpdatedUtc: '2026-06-15T01:00:00Z',
        exciterAdc: 100,
        fwdAdc: 1800,
        revAdc: 120,
        fwdWatts: 44.02,
        refWatts: 0.1,
        swr: 1.1,
      },
      p2: {
        packets: 0,
        lastUpdatedUtc: null,
        exciterAdc: null,
        fwdAdc: null,
        revAdc: null,
        fwdWatts: null,
        refWatts: null,
        swr: null,
      },
      diagnosticRecommendation: 'PA telemetry is decoded with the same board calibration.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchRadioPowerCalibration();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/power-calibration');
    expect(init?.method).toBeUndefined();
    expect(status.activeProtocol).toBe('P1');
    expect(status.connectedBoard).toBe('OrionMkII');
    expect(status.bridgeVolt).toBe(0.12);
    expect(status.calibrationFallbackApplied).toBe(false);
    expect(status.p1.fwdAdc).toBe(1800);
    expect(status.p1.fwdWatts).toBe(44.02);
    expect(status.p2.fwdWatts).toBeNull();
  });

  it('fetchRadioSupplyAlarms reads supply telemetry and advisory alarm state', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      supportsSupplyTelemetry: true,
      adcSupplyMv: 50,
      activeThresholdsConfigured: false,
      alarmActive: false,
      alarmStatus: 'telemetry-ready',
      p1: {
        packets: 0,
        lastUpdatedUtc: null,
        supplyVoltsAdc: null,
        supplyVolts: null,
        rawScaledSupplyVolts: null,
        supplyVoltsTrusted: false,
        scaleStatus: 'missing',
      },
      p2: {
        packets: 42,
        lastUpdatedUtc: '2026-06-15T01:00:00Z',
        supplyVoltsAdc: 276,
        supplyVolts: 13.8,
        rawScaledSupplyVolts: 13.8,
        supplyVoltsTrusted: true,
        scaleStatus: 'trusted',
      },
      diagnosticRecommendation: 'Live supply voltage is decoded and scaled.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchRadioSupplyAlarms();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/supply-alarms');
    expect(init?.method).toBeUndefined();
    expect(status.activeProtocol).toBe('P2');
    expect(status.supportsSupplyTelemetry).toBe(true);
    expect(status.activeThresholdsConfigured).toBe(false);
    expect(status.alarmStatus).toBe('telemetry-ready');
    expect(status.p2.supplyVoltsAdc).toBe(276);
    expect(status.p2.supplyVolts).toBe(13.8);
    expect(status.p2.rawScaledSupplyVolts).toBe(13.8);
    expect(status.p2.supplyVoltsTrusted).toBe(true);
    expect(status.p2.scaleStatus).toBe('trusted');
  });

  it('fetchRadioPaThermalDiagnostics reads decoded and unmapped thermal status', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      connectedBoard: 'OrionMkII',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      supportsTemperatureTelemetry: true,
      temperatureDecoded: false,
      temperatureAvailable: false,
      source: 'p2-g2-temperature-slot-unmapped',
      status: 'p2-g2-temp-unmapped',
      tempC: null,
      rawAdc: null,
      ageMs: null,
      lastUpdatedUtc: null,
      warningTempC: 50,
      criticalTempC: 55,
      manualReference: 'G2 manual documents temperature sensors and fan cooling.',
      diagnosticRecommendation: 'Capture P2 diagnostics markers before arming thermal inhibit.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchRadioPaThermalDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/pa-thermal');
    expect(init?.method).toBeUndefined();
    expect(status.activeProtocol).toBe('P2');
    expect(status.connectedBoard).toBe('OrionMkII');
    expect(status.supportsTemperatureTelemetry).toBe(true);
    expect(status.temperatureDecoded).toBe(false);
    expect(status.temperatureAvailable).toBe(false);
    expect(status.source).toBe('p2-g2-temperature-slot-unmapped');
    expect(status.status).toBe('p2-g2-temp-unmapped');
    expect(status.tempC).toBeNull();
    expect(status.warningTempC).toBe(50);
    expect(status.criticalTempC).toBe(55);
  });

  it('fetchG2SensorMappingDiagnostics reads mapped fields and unmapped manual sensors', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      connectedBoard: 'OrionMkII',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      g2Class: true,
      p2Attached: true,
      p2Packets: 250,
      p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
      status: 'manual-sensors-unmapped',
      mappedSensors: [
        {
          id: 'supply-voltage',
          label: 'Supply volts ADC',
          telemetryPath: 'p2.supplyVoltsAdc',
          source: 'Thetis P2 hi-priority word 0x2D',
          rawValue: 1613,
          status: 'mapped-live',
          notes: 'Raw supply-voltage ADC is live.',
        },
      ],
      unmappedManualSensors: [
        {
          id: 'driver-current',
          label: 'Driver current',
          manualEvidence: 'biascheck reads Driver Current.',
          currentTelemetryStatus: 'p2-g2-driver-current-unmapped',
          requiredCapture: 'Capture receive idle plus staged TUN drive markers.',
          safetyClass: 'tx-monitoring-only',
        },
      ],
      candidateWords: [
        {
          offset: 30,
          hexOffset: '0x1E',
          known: null,
          last: 5376,
          min: 0,
          max: 7168,
          changeCount: 15,
          status: 'unknown-variable',
          mappingHint: 'Create before/after markers.',
        },
      ],
      manualReference: 'G2 manual documents current, voltage, temperature sensors.',
      diagnosticRecommendation: 'Use Settings > Hardware > Mapping Capture.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const status = await fetchG2SensorMappingDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/g2-sensors');
    expect(init?.method).toBeUndefined();
    expect(status.g2Class).toBe(true);
    expect(status.p2Packets).toBe(250);
    expect(status.mappedSensors[0]?.rawValue).toBe(1613);
    expect(status.unmappedManualSensors[0]?.id).toBe('driver-current');
    expect(status.candidateWords[0]?.offset).toBe(30);
    expect(status.diagnosticRecommendation).toContain('Mapping Capture');
  });

  it('fetchRadioNetworkProfile reads active transport counters', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      connectionStatus: 'Connected',
      endpoint: '192.168.1.10:1024',
      activeProtocol: 'P2',
      sampleRateHz: 384000,
      connectedBoard: 'OrionMkII',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      transport: 'udp',
      p1: {
        attached: false,
        totalFrames: 0,
        droppedFrames: 0,
        dropRatioPct: 0,
        hiPriorityPackets: null,
        psPairedPackets: null,
      },
      p2: {
        attached: true,
        totalFrames: 12000,
        droppedFrames: 3,
        dropRatioPct: 0.025,
        hiPriorityPackets: 900,
        psPairedPackets: null,
      },
      healthStatus: 'loss-detected',
      diagnosticRecommendation: 'UDP sequence gaps are present.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const profile = await fetchRadioNetworkProfile();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/network-profile');
    expect(init?.method).toBeUndefined();
    expect(profile.connectionStatus).toBe('Connected');
    expect(profile.endpoint).toBe('192.168.1.10:1024');
    expect(profile.activeProtocol).toBe('P2');
    expect(profile.sampleRateHz).toBe(384000);
    expect(profile.p2.attached).toBe(true);
    expect(profile.p2.droppedFrames).toBe(3);
    expect(profile.p2.hiPriorityPackets).toBe(900);
    expect(profile.healthStatus).toBe('loss-detected');
  });

  it('fetchUserIoLabels reads default P2 user I/O labels and values', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      p2Attached: true,
      p2Packets: 12,
      p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
      lines: [
        {
          id: 'userAdc0',
          kind: 'analog',
          label: 'User ADC 0',
          rawAdc: 32768,
          normalizedPct: 50,
          digitalState: null,
        },
        {
          id: 'userDigital0',
          kind: 'digital',
          label: 'User Digital 0',
          rawAdc: null,
          normalizedPct: null,
          digitalState: true,
        },
      ],
      diagnosticRecommendation: 'P2 user I/O lines are decoded with default labels.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const labels = await fetchUserIoLabels();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/user-io/labels');
    expect(init?.method).toBeUndefined();
    expect(labels.activeProtocol).toBe('P2');
    expect(labels.p2Attached).toBe(true);
    expect(labels.lines[0]?.rawAdc).toBe(32768);
    expect(labels.lines[0]?.normalizedPct).toBe(50);
    expect(labels.lines[1]?.digitalState).toBe(true);
  });

  it('fetchUserIoActions reads unarmed action binding readiness', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      p2Attached: true,
      p2Packets: 12,
      p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
      actionBindingsConfigured: false,
      lines: [
        {
          id: 'userDigital7',
          kind: 'digital',
          label: 'User Digital 7',
          rawAdc: null,
          normalizedPct: null,
          digitalState: false,
        },
      ],
      diagnosticRecommendation: 'User I/O action bindings are intentionally unarmed.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const actions = await fetchUserIoActions();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/user-io/actions');
    expect(init?.method).toBeUndefined();
    expect(actions.actionBindingsConfigured).toBe(false);
    expect(actions.lines[0]?.id).toBe('userDigital7');
    expect(actions.lines[0]?.digitalState).toBe(false);
  });

  it('fetchRadioDigInDiagnostics reads G2 TX Disable mapping', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      schemaVersion: 1,
      activeProtocol: 'P2',
      connectedBoard: 'OrionMkII',
      effectiveBoard: 'OrionMkII',
      orionMkIIVariant: 'G2',
      p2Attached: true,
      p2Packets: 18,
      p2LastUpdatedUtc: '2026-06-15T01:00:00Z',
      userDigitalIn: 0,
      txDisableLineId: 'userDigital1',
      txDisableLineName: 'User I/O IO5',
      txDisableBit: 1,
      txDisableRawHigh: false,
      txDisableActive: true,
      txDisablePolarity: 'active-low',
      txDisableMappingStatus: 'thetis-p2-saturn-io5',
      txInhibitBehaviorArmed: false,
      cwKeyTipSource: 'p2.dotIn',
      cwKeyTipDown: true,
      cwDashInputDown: false,
      manualReference: 'ANAN G2 manual.',
      diagnosticRecommendation: 'Dig In TX Disable is active-low.',
      generatedUtc: '2026-06-15T01:00:01Z',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const digIn = await fetchRadioDigInDiagnostics();

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/radio/dig-in');
    expect(init?.method).toBeUndefined();
    expect(digIn.txDisableLineId).toBe('userDigital1');
    expect(digIn.txDisableBit).toBe(1);
    expect(digIn.txDisableRawHigh).toBe(false);
    expect(digIn.txDisableActive).toBe(true);
    expect(digIn.txInhibitBehaviorArmed).toBe(false);
    expect(digIn.cwKeyTipDown).toBe(true);
  });

  it('raises ApiError with server-provided error text on 400', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn<typeof fetch>()
        .mockResolvedValue(jsonResponse({ error: 'invalid sample rate' }, 400)),
    );
    await expect(setSampleRate(999 as unknown as 48_000)).rejects.toMatchObject(
      { name: 'ApiError', status: 400, message: 'invalid sample rate' },
    );
  });

  it('falls back to status text when body is non-JSON', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('oops', { status: 500, statusText: 'Internal' }),
      ),
    );
    try {
      await setPreamp(true);
      expect.fail('should have thrown');
    } catch (e) {
      expect(e).toBeInstanceOf(ApiError);
      expect((e as ApiError).status).toBe(500);
    }
  });

  it('setMicGain posts { db } to /api/mic-gain and returns echoed value', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ micGainDb: 12 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(setMicGain(12)).resolves.toEqual({ micGainDb: 12 });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/mic-gain');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ db: 12 });
  });

  it('setMicGain rethrows 404 so missing backend routes roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    await expect(setMicGain(5)).rejects.toMatchObject({
      name: 'ApiError',
      status: 404,
    });
  });

  it('setMicGain rethrows non-404 errors so the slider can roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({ error: 'db out of range' }, 400),
      ),
    );
    await expect(setMicGain(99)).rejects.toMatchObject({
      name: 'ApiError',
      status: 400,
    });
  });

  it('setLevelerMaxGain posts { gain } to /api/tx/leveler-max-gain and returns echoed value', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ levelerMaxGainDb: 7.5 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(setLevelerMaxGain(7.5)).resolves.toEqual({
      levelerMaxGainDb: 7.5,
    });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/leveler-max-gain');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ gain: 7.5 });
  });

  it('setLevelerMaxGain rethrows 404 so missing backend routes roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    await expect(setLevelerMaxGain(5)).rejects.toMatchObject({
      name: 'ApiError',
      status: 404,
    });
  });

  it('setLevelerMaxGain rethrows non-404 errors so the slider can roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({ error: 'gain out of range' }, 400),
      ),
    );
    await expect(setLevelerMaxGain(99)).rejects.toMatchObject({
      name: 'ApiError',
      status: 400,
    });
  });

  it('setTun posts { on } to /api/tx/tun and returns echoed value', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ tunOn: true }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(setTun(true)).resolves.toEqual({ tunOn: true });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/tun');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ on: true });
  });

  it('setTxVfo posts the selected VFO enum and normalizes returned state', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ txVfo: 1, vfoHz: 14_200_000 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(setTxVfo('B')).resolves.toMatchObject({ txVfo: 'B' });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/vfo');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ txVfo: 1 });
  });

  it('setTun rethrows 404 so missing backend routes roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    await expect(setTun(true)).rejects.toMatchObject({
      name: 'ApiError',
      status: 404,
    });
  });

  it('setTun rethrows non-404 errors so the button can roll back', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({ error: 'not connected' }, 409),
      ),
    );
    await expect(setTun(true)).rejects.toMatchObject({
      name: 'ApiError',
      status: 409,
    });
  });

  it('setAgc posts { agc } with string mode to /api/rx/agc', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    const cfg: AgcConfigDto = { mode: 'Fast' };
    await setAgc(cfg);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/agc');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ agc: { mode: 'Fast' } });
  });

  it('setAgc posts custom params when provided', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    const cfg: AgcConfigDto = {
      mode: 'Custom',
      slope: 5,
      decayMs: 500,
      hangMs: 300,
      hangThreshold: 25,
    };
    await setAgc(cfg);
    const [, init] = fetchMock.mock.calls[0]!;
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ agc: cfg });
  });

  it('setSquelch posts { squelch } to /api/rx/squelch', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    const cfg: SquelchConfigDto = {
      enabled: true,
      level: 42,
      adaptive: true,
      fixedSensitivity: 70,
    };
    await setSquelch(cfg);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/rx/squelch');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ squelch: cfg });
  });

  it('setTxLeveling posts { txLeveling } to /api/tx/leveling', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse(okState));
    vi.stubGlobal('fetch', fetchMock);

    const cfg: TxLevelingConfigDto = {
      alcMaxGainDb: 6,
      alcDecayMs: 20,
      levelerEnabled: false,
      levelerDecayMs: 250,
      compressorEnabled: true,
      compressorGainDb: 9,
    };
    await setTxLeveling(cfg);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/tx/leveling');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ txLeveling: cfg });
  });

  it('fetchCfcPresets gets saved CFC presets and normalizes configs', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({
        presets: [
          {
            name: ' Ragchew ',
            config: {
              enabled: true,
              preCompDb: 2,
              bands: [
                { freqHz: 80, compLevelDb: 1, postGainDb: -1 },
              ],
            },
            createdUtc: '2026-06-15T10:00:00Z',
            updatedUtc: '2026-06-15T10:01:00Z',
          },
          { name: '', config: CFC_CONFIG_DEFAULT },
        ],
      }));
    vi.stubGlobal('fetch', fetchMock);

    const presets = await fetchCfcPresets();

    expect(fetchMock.mock.calls[0]![0]).toBe('/api/tx/cfc/presets');
    expect(presets).toHaveLength(1);
    expect(presets[0]!.name).toBe('Ragchew');
    expect(presets[0]!.config.enabled).toBe(true);
    expect(presets[0]!.config.postEqEnabled).toBe(false);
    expect(presets[0]!.config.bands).toHaveLength(10);
    expect(presets[0]!.config.bands[0]).toEqual({ freqHz: 80, compLevelDb: 1, postGainDb: -1 });
    expect(presets[0]!.config.bands[1]).toEqual(CFC_CONFIG_DEFAULT.bands[1]);
  });

  it('saveCfcPreset puts { config } to the encoded CFC preset route', async () => {
    const cfg: CfcConfigDto = {
      ...CFC_CONFIG_DEFAULT,
      enabled: true,
      preCompDb: 1.5,
      bands: CFC_CONFIG_DEFAULT.bands.map((band, idx) =>
        idx === 4 ? { ...band, compLevelDb: 5.5 } : { ...band },
      ),
    };
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({
        name: 'DX Wide',
        config: cfg,
        createdUtc: '2026-06-15T10:00:00Z',
        updatedUtc: '2026-06-15T10:02:00Z',
      }));
    vi.stubGlobal('fetch', fetchMock);

    const saved = await saveCfcPreset('DX Wide', cfg);
    const [url, init] = fetchMock.mock.calls[0]!;

    expect(url).toBe('/api/tx/cfc/presets/DX%20Wide');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ config: cfg });
    expect(saved.name).toBe('DX Wide');
    expect(saved.config.bands[4]!.compLevelDb).toBe(5.5);
  });
});

describe('normalizeAgcMode', () => {
  it('accepts valid PascalCase string values', () => {
    expect(normalizeAgcMode('Fixed')).toBe('Fixed');
    expect(normalizeAgcMode('Long')).toBe('Long');
    expect(normalizeAgcMode('Slow')).toBe('Slow');
    expect(normalizeAgcMode('Med')).toBe('Med');
    expect(normalizeAgcMode('Fast')).toBe('Fast');
    expect(normalizeAgcMode('Custom')).toBe('Custom');
  });

  it('maps numeric ordinals (0=Fixed 1=Long 2=Slow 3=Med 4=Fast 5=Custom)', () => {
    expect(normalizeAgcMode(0)).toBe('Fixed');
    expect(normalizeAgcMode(1)).toBe('Long');
    expect(normalizeAgcMode(2)).toBe('Slow');
    expect(normalizeAgcMode(3)).toBe('Med');
    expect(normalizeAgcMode(4)).toBe('Fast');
    expect(normalizeAgcMode(5)).toBe('Custom');
  });

  it('falls back to Med for unknown strings', () => {
    expect(normalizeAgcMode('Bogus')).toBe('Med');
    expect(normalizeAgcMode('auto')).toBe('Med');
    expect(normalizeAgcMode('')).toBe('Med');
  });

  it('falls back to Med for out-of-range numeric index', () => {
    expect(normalizeAgcMode(6)).toBe('Med');
    expect(normalizeAgcMode(99)).toBe('Med');
    expect(normalizeAgcMode(-1)).toBe('Med');
  });

  it('falls back to Med for null/undefined/object', () => {
    expect(normalizeAgcMode(null)).toBe('Med');
    expect(normalizeAgcMode(undefined)).toBe('Med');
    expect(normalizeAgcMode({})).toBe('Med');
  });
});

describe('normalizeAgc', () => {
  it('returns AGC_CONFIG_DEFAULT for null', () => {
    expect(normalizeAgc(null)).toEqual(AGC_CONFIG_DEFAULT);
  });

  it('returns AGC_CONFIG_DEFAULT for undefined', () => {
    expect(normalizeAgc(undefined)).toEqual(AGC_CONFIG_DEFAULT);
  });

  it('returns AGC_CONFIG_DEFAULT for a missing agc object (normalizeState path)', () => {
    const s = normalizeState({});
    expect(s.agc).toEqual(AGC_CONFIG_DEFAULT);
  });

  it('round-trips a valid AgcConfigDto through normalizeState', () => {
    const s = normalizeState({
      status: 'Connected',
      mode: 'USB',
      agc: { mode: 'Custom', slope: 5, decayMs: 500, hangMs: 300, hangThreshold: 25 },
    });
    expect(s.agc.mode).toBe('Custom');
    expect(s.agc.slope).toBe(5);
    expect(s.agc.decayMs).toBe(500);
    expect(s.agc.hangMs).toBe(300);
    expect(s.agc.hangThreshold).toBe(25);
  });

  it('normalizes a garbage mode string inside an agc object to Med', () => {
    const s = normalizeState({ agc: { mode: 'garbage' } });
    expect(s.agc.mode).toBe('Med');
  });

  it('normalizes an out-of-range numeric mode inside an agc object to Med', () => {
    const s = normalizeState({ agc: { mode: 999 } });
    expect(s.agc.mode).toBe('Med');
  });
});

describe('normalizeSquelch', () => {
  it('returns SQUELCH_CONFIG_DEFAULT for null', () => {
    expect(normalizeSquelch(null)).toEqual(SQUELCH_CONFIG_DEFAULT);
  });

  it('returns SQUELCH_CONFIG_DEFAULT for undefined', () => {
    expect(normalizeSquelch(undefined)).toEqual(SQUELCH_CONFIG_DEFAULT);
  });

  it('returns SQUELCH_CONFIG_DEFAULT for a non-object', () => {
    expect(normalizeSquelch('garbage')).toEqual(SQUELCH_CONFIG_DEFAULT);
    expect(normalizeSquelch(42)).toEqual(SQUELCH_CONFIG_DEFAULT);
  });

  it('returns SQUELCH_CONFIG_DEFAULT for a missing squelch object (normalizeState path)', () => {
    const s = normalizeState({});
    expect(s.squelch).toEqual(SQUELCH_CONFIG_DEFAULT);
  });

  it('round-trips a valid SquelchConfigDto through normalizeState', () => {
    const s = normalizeState({
      status: 'Connected',
      mode: 'USB',
      squelch: { enabled: true, level: 73, adaptive: false, fixedSensitivity: 84 },
    });
    expect(s.squelch.enabled).toBe(true);
    expect(s.squelch.level).toBe(73);
    expect(s.squelch.adaptive).toBe(false);
    expect(s.squelch.fixedSensitivity).toBe(84);
  });

  it('defaults enabled to false when the field is missing or garbage', () => {
    expect(normalizeSquelch({ level: 20 }).enabled).toBe(false);
    expect(normalizeSquelch({ enabled: 'yes', level: 20 }).enabled).toBe(false);
  });

  it('defaults level to 0 for a missing or non-numeric level', () => {
    expect(normalizeSquelch({ enabled: true }).level).toBe(0);
    expect(normalizeSquelch({ enabled: true, level: 'loud' }).level).toBe(0);
  });

  it('clamps an out-of-range level into 0..100', () => {
    expect(normalizeSquelch({ enabled: true, level: -5 }).level).toBe(0);
    expect(normalizeSquelch({ enabled: true, level: 250 }).level).toBe(100);
  });

  it('rounds a fractional level to the nearest integer', () => {
    expect(normalizeSquelch({ enabled: true, level: 42.6 }).level).toBe(43);
  });

  it('defaults adaptive mode on for older payloads', () => {
    expect(normalizeSquelch({ enabled: true, level: 20 }).adaptive).toBe(true);
    expect(normalizeSquelch({ enabled: true, level: 20, adaptive: 'yes' }).adaptive).toBe(true);
  });

  it('defaults fixed sensitivity for older payloads', () => {
    expect(normalizeSquelch({ enabled: true, level: 20 }).fixedSensitivity).toBe(70);
    expect(normalizeSquelch({ enabled: true, level: 20, fixedSensitivity: 'hot' }).fixedSensitivity).toBe(70);
  });

  it('clamps and rounds fixed sensitivity into 0..100', () => {
    expect(normalizeSquelch({ enabled: true, fixedSensitivity: -5 }).fixedSensitivity).toBe(0);
    expect(normalizeSquelch({ enabled: true, fixedSensitivity: 250 }).fixedSensitivity).toBe(100);
    expect(normalizeSquelch({ enabled: true, fixedSensitivity: 42.6 }).fixedSensitivity).toBe(43);
  });
});

describe('normalizeTxLeveling', () => {
  it('returns TX_LEVELING_CONFIG_DEFAULT for null/undefined', () => {
    expect(normalizeTxLeveling(null)).toEqual(TX_LEVELING_CONFIG_DEFAULT);
    expect(normalizeTxLeveling(undefined)).toEqual(TX_LEVELING_CONFIG_DEFAULT);
  });

  it('returns TX_LEVELING_CONFIG_DEFAULT for a non-object', () => {
    expect(normalizeTxLeveling('garbage')).toEqual(TX_LEVELING_CONFIG_DEFAULT);
    expect(normalizeTxLeveling(42)).toEqual(TX_LEVELING_CONFIG_DEFAULT);
  });

  it('returns the default for a missing txLeveling object (normalizeState path)', () => {
    const s = normalizeState({});
    expect(s.txLeveling).toEqual(TX_LEVELING_CONFIG_DEFAULT);
  });

  it('round-trips a valid TxLevelingConfigDto through normalizeState', () => {
    const s = normalizeState({
      status: 'Connected',
      mode: 'USB',
      txLeveling: {
        alcMaxGainDb: 6,
        alcDecayMs: 20,
        levelerEnabled: false,
        levelerDecayMs: 250,
        compressorEnabled: true,
        compressorGainDb: 9,
      },
    });
    expect(s.txLeveling).toEqual({
      alcMaxGainDb: 6,
      alcDecayMs: 20,
      levelerEnabled: false,
      levelerDecayMs: 250,
      compressorEnabled: true,
      compressorGainDb: 9,
    });
  });

  it('falls back per-field for missing or non-numeric values', () => {
    const out = normalizeTxLeveling({ alcMaxGainDb: 'loud', levelerEnabled: 'yes' });
    expect(out.alcMaxGainDb).toBe(TX_LEVELING_CONFIG_DEFAULT.alcMaxGainDb);
    // strict boolean coercion: a string is not a boolean → default true
    expect(out.levelerEnabled).toBe(TX_LEVELING_CONFIG_DEFAULT.levelerEnabled);
  });

  it('clamps out-of-range numerics into their Thetis ranges', () => {
    expect(normalizeTxLeveling({ alcMaxGainDb: -5 }).alcMaxGainDb).toBe(0);
    expect(normalizeTxLeveling({ alcMaxGainDb: 500 }).alcMaxGainDb).toBe(120);
    expect(normalizeTxLeveling({ alcDecayMs: 0 }).alcDecayMs).toBe(1);
    expect(normalizeTxLeveling({ alcDecayMs: 999 }).alcDecayMs).toBe(50);
    expect(normalizeTxLeveling({ levelerDecayMs: 0 }).levelerDecayMs).toBe(1);
    expect(normalizeTxLeveling({ levelerDecayMs: 99999 }).levelerDecayMs).toBe(5000);
    expect(normalizeTxLeveling({ compressorGainDb: -5 }).compressorGainDb).toBe(0);
    expect(normalizeTxLeveling({ compressorGainDb: 99 }).compressorGainDb).toBe(20);
  });

  it('rounds fractional integer fields', () => {
    expect(normalizeTxLeveling({ alcDecayMs: 12.6 }).alcDecayMs).toBe(13);
    expect(normalizeTxLeveling({ levelerDecayMs: 100.4 }).levelerDecayMs).toBe(100);
  });
});
