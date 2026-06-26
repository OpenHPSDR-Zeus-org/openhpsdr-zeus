// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the WSJT-X logged-QSO UDP broadcaster (outbound QSO push to
// JTAlert / Log4OM / GridTracker / N1MM). Mirrors api/cat.ts but has no port
// test (UDP has no listener) and applies live (no restart). Mirrors api/cat.ts.

import { ApiError } from './client';

export type WsjtxStatus = {
  enabled: boolean;
  host: string;
  port: number;
  instanceId: string;
};

export type WsjtxConfig = {
  enabled: boolean;
  host: string;
  port: number;
};

const DEFAULT_PORT = 2237;

function normalizeStatus(raw: unknown): WsjtxStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' ? r.port : DEFAULT_PORT,
    instanceId: typeof r.instanceId === 'string' ? r.instanceId : 'WSJT-X',
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

export function getWsjtxStatus(signal?: AbortSignal): Promise<WsjtxStatus> {
  return jsonFetch('/api/wsjtx/status', { signal }, normalizeStatus);
}

export function postWsjtxConfig(cfg: WsjtxConfig, signal?: AbortSignal): Promise<WsjtxStatus> {
  return jsonFetch(
    '/api/wsjtx/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}
