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
import {
  ApiError,
  connect,
  NR_CONFIG_DEFAULT,
  normalizeMode,
  normalizeNbMode,
  normalizeNr,
  normalizeNrMode,
  normalizeState,
  normalizeStatus,
  setAgcTop,
  setAttenuator,
  setAutoAtt,
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
  });
  it('defaults ctunEnabled to false on a legacy server (no field)', () => {
    // CTUN re-introduced (Christiano fork) — a server that predates the field
    // must hydrate CTUN OFF so the behaviour stays byte-identical to develop.
    expect(normalizeState({}).ctunEnabled).toBe(false);
    expect(normalizeState({ ctunEnabled: 'on' }).ctunEnabled).toBe(false);
  });
  it('reads ctunEnabled from the server (now authoritative, not ignored)', () => {
    expect(normalizeState({ ctunEnabled: true }).ctunEnabled).toBe(true);
    expect(normalizeState({ ctunEnabled: false }).ctunEnabled).toBe(false);
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
});
