// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
  SquelchConfigDto,
  TxLevelingConfigDto,
} from './client';
import {
  AGC_CONFIG_DEFAULT,
  ApiError,
  connect,
  NR_CONFIG_DEFAULT,
  SQUELCH_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  createHardwareDiagnosticsMarker,
  fetchAdcProtection,
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
  normalizeTxLeveling,
  setAgc,
  setAgcTop,
  setSquelch,
  setTxLeveling,
  setAttenuator,
  setAutoAtt,
  setAdcProtection,
  setLevelerMaxGain,
  setMicGain,
  setMode,
  setNr,
  setPreamp,
  setSampleRate,
  setTun,
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
  });
  it('falls back to USB on garbage', () => {
    expect(normalizeMode('nope')).toBe('USB');
    expect(normalizeMode(42)).toBe('USB');
  });
});

describe('normalizeState', () => {
  it('reads a camelCase StateDto with numeric enums', () => {
    const s = normalizeState({
      status: 2,
      endpoint: '192.168.100.21:1024',
      vfoHz: 14_200_000,
      mode: 1,
      filterLowHz: 150,
      filterHighHz: 2850,
      sampleRate: 192_000,
    });
    expect(s.status).toBe('Connected');
    expect(s.mode).toBe('USB');
    expect(s.endpoint).toBe('192.168.100.21:1024');
    expect(s.vfoHz).toBe(14_200_000);
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
    expect(s.mode).toBe('USB');
    expect(s.nr).toEqual(NR_CONFIG_DEFAULT);
    expect(s.zoomLevel).toBe(1);
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
    expect(normalizeNbMode('Nb1')).toBe('Nb1');
  });
  it('maps numeric ordinals', () => {
    expect(normalizeNrMode(0)).toBe('Off');
    expect(normalizeNrMode(1)).toBe('Anr');
    expect(normalizeNrMode(2)).toBe('Emnr');
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
      .toEqual({ mode: 0 });

    await setMode('DIGU');
    expect(JSON.parse((fetchMock.mock.calls[1]![1]?.body ?? '') as string))
      .toEqual({ mode: 9 });
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
    expect(diag.frontendDspScene.available).toBe(true);
    expect(diag.frontendDspScene.status).toBe('fresh');
    expect(diag.frontendDspScene.fresh).toBe(true);
    expect(diag.frontendDspScene.stale).toBe(false);
    expect(diag.frontendDspScene.diagnosticRecommendation).toContain('fresh');
    expect(diag.frontendDspScene.signalProfile).toBe('dx');
    expect(diag.frontendDspScene.smartNrRecommendation).toBe('Hold headroom; use Smart NR/filtering');
    expect(diag.frontendDspScene.coherentPeakCount).toBe(2);
    expect(diag.frontendDspScene.coherentSubthresholdSignal).toBe(true);
    expect(diag.mapping.schemaVersion).toBe(2);
    expect(diag.mapping.markers[0]?.label).toBe('RX2 on');
    expect(diag.featureSurfaces[0]?.id).toBe('hardware.mapping.correlation');
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
      sourceClientId: 'frontend-test',
      mode: 'USB',
      signalProfile: 'dx',
      smartNrProfile: 'NR4',
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
    }));
    vi.stubGlobal('fetch', fetchMock);

    const scene = await publishFrontendDspSceneDiagnostics({
      sourceAtUtc: '2026-06-15T00:59:58Z',
      sourceClientId: 'frontend-test',
      mode: 'USB',
      signalProfile: 'dx',
      smartNrProfile: 'NR4',
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
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
      maxSnrDb: 12.5,
      peakCount: 1,
      coherentSubthresholdSignal: true,
    });
    expect(scene.available).toBe(true);
    expect(scene.status).toBe('fresh');
    expect(scene.fresh).toBe(true);
    expect(scene.sourceAtUtc).toBe('2026-06-15T00:59:58Z');
    expect(scene.sourceAgeMs).toBe(2012);
    expect(scene.signalProfile).toBe('dx');
    expect(scene.smartNrProfile).toBe('NR4');
    expect(scene.coherentSubthresholdSignal).toBe(true);
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

  it('setMicGain treats 404 as accepted (backend not landed yet)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    // Must not throw — the slider keeps its optimistic value rather than rolling back.
    await expect(setMicGain(5)).resolves.toEqual({ micGainDb: 5 });
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

  it('setLevelerMaxGain treats 404 as accepted (backend not landed yet)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    await expect(setLevelerMaxGain(5)).resolves.toEqual({
      levelerMaxGainDb: 5,
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

  it('setTun treats 404 as accepted (backend not landed yet)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('Not Found', { status: 404, statusText: 'Not Found' }),
      ),
    );
    await expect(setTun(true)).resolves.toEqual({ tunOn: true });
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

    const cfg: SquelchConfigDto = { enabled: true, level: 42 };
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
      squelch: { enabled: true, level: 73 },
    });
    expect(s.squelch.enabled).toBe(true);
    expect(s.squelch.level).toBe(73);
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
