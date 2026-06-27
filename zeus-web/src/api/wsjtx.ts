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

// "unicast" → one logger on Host:Port. "multicast" → many loggers receive the
// same stream via MulticastGroup/MulticastTtl (the only way to feed e.g.
// GridTracker AND Log4OM at once, since unicast hands the port to a single
// binder). Mirrors WsjtxRuntimeConfig.Transport in Zeus.Contracts.
export type WsjtxTransport = 'unicast' | 'multicast';

export type WsjtxStatus = {
  enabled: boolean;
  host: string;
  port: number;
  instanceId: string;
  transport: WsjtxTransport;
  multicastGroup: string;
  multicastTtl: number;
  sendQsoLogged: boolean;
  sendLiveDecodes: boolean;
};

export type WsjtxConfig = {
  enabled: boolean;
  host: string;
  port: number;
  instanceId: string;
  transport: WsjtxTransport;
  multicastGroup: string;
  multicastTtl: number;
  sendQsoLogged: boolean;
  sendLiveDecodes: boolean;
};

export const WSJTX_DEFAULT_PORT = 2237;
export const WSJTX_DEFAULT_GROUP = '224.0.0.73';
export const WSJTX_DEFAULT_TTL = 1;
export const WSJTX_DEFAULT_INSTANCE = 'WSJT-X';

function normalizeTransport(v: unknown): WsjtxTransport {
  return v === 'multicast' ? 'multicast' : 'unicast';
}

function normalizeStatus(raw: unknown): WsjtxStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' ? r.port : WSJTX_DEFAULT_PORT,
    instanceId: typeof r.instanceId === 'string' ? r.instanceId : WSJTX_DEFAULT_INSTANCE,
    transport: normalizeTransport(r.transport),
    multicastGroup:
      typeof r.multicastGroup === 'string' ? r.multicastGroup : WSJTX_DEFAULT_GROUP,
    multicastTtl: typeof r.multicastTtl === 'number' ? r.multicastTtl : WSJTX_DEFAULT_TTL,
    sendQsoLogged: Boolean(r.sendQsoLogged),
    sendLiveDecodes: Boolean(r.sendLiveDecodes),
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
