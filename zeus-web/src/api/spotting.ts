// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// REST client for the digital-mode spotting uploaders (FT8/FT4 -> PSK Reporter,
// WSPR -> WSPRnet). Mirrors api/wsjtx.ts: no port test (UDP/HTTP has no
// listener) and applies live (no restart). Both uploaders default OFF.

import { ApiError } from './client';

export type SpottingStatus = {
  pskReporterEnabled: boolean;
  wsprnetEnabled: boolean;
  callsign: string;
  grid: string;
  identityResolved: boolean;
};

export type SpottingConfig = {
  pskReporterEnabled: boolean;
  wsprnetEnabled: boolean;
  callsign: string;
  grid: string;
};

function normalizeStatus(raw: unknown): SpottingStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    pskReporterEnabled: Boolean(r.pskReporterEnabled),
    wsprnetEnabled: Boolean(r.wsprnetEnabled),
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    grid: typeof r.grid === 'string' ? r.grid : '',
    identityResolved: Boolean(r.identityResolved),
  };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (
        body &&
        typeof body === 'object' &&
        'error' in body &&
        typeof (body as { error: unknown }).error === 'string'
      ) {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function getSpottingStatus(signal?: AbortSignal): Promise<SpottingStatus> {
  return jsonFetch('/api/spotting/status', { signal }, normalizeStatus);
}

export function postSpottingConfig(cfg: SpottingConfig, signal?: AbortSignal): Promise<SpottingStatus> {
  return jsonFetch(
    '/api/spotting/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}
