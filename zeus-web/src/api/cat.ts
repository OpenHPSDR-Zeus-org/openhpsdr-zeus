// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the CAT (Kenwood TS-2000 over TCP) server. Mirrors api/tci.ts.

import { ApiError } from './client';

export type CatStatus = {
  currentlyEnabled: boolean;
  currentPort: number;
  currentBindAddress: string;
  pendingEnabled: boolean;
  pendingPort: number;
  pendingBindAddress: string;
  clientCount: number;
  portAvailable: boolean;
  requiresRestart: boolean;
  error: string | null;
};

export type CatConfig = {
  enabled: boolean;
  bindAddress: string;
  port: number;
};

export type CatTestResult = { ok: boolean; error: string | null };

const DEFAULT_PORT = 19090;

function normalizeStatus(raw: unknown): CatStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    currentlyEnabled: Boolean(r.currentlyEnabled),
    currentPort: typeof r.currentPort === 'number' ? r.currentPort : DEFAULT_PORT,
    currentBindAddress: typeof r.currentBindAddress === 'string' ? r.currentBindAddress : '127.0.0.1',
    pendingEnabled: Boolean(r.pendingEnabled),
    pendingPort: typeof r.pendingPort === 'number' ? r.pendingPort : DEFAULT_PORT,
    pendingBindAddress: typeof r.pendingBindAddress === 'string' ? r.pendingBindAddress : '127.0.0.1',
    clientCount: typeof r.clientCount === 'number' ? r.clientCount : 0,
    portAvailable: Boolean(r.portAvailable),
    requiresRestart: Boolean(r.requiresRestart),
    error: typeof r.error === 'string' && r.error.length > 0 ? r.error : null,
  };
}

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object' && 'error' in body && typeof (body as { error: unknown }).error === 'string') {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function getCatStatus(signal?: AbortSignal): Promise<CatStatus> {
  return jsonFetch('/api/cat/status', { signal }, normalizeStatus);
}

export function postCatConfig(cfg: CatConfig, signal?: AbortSignal): Promise<CatStatus> {
  return jsonFetch(
    '/api/cat/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function testCatPort(bindAddress: string, port: number, signal?: AbortSignal): Promise<CatTestResult> {
  return jsonFetch(
    '/api/cat/test',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ bindAddress, port }),
      signal,
    },
    (raw) => {
      const r = (raw ?? {}) as Record<string, unknown>;
      return { ok: Boolean(r.ok), error: typeof r.error === 'string' && r.error ? r.error : null };
    },
  );
}
