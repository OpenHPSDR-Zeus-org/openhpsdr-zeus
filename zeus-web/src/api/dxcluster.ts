// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { ApiError } from './client';

export type DxClusterConnectionState =
  | 'Disconnected'
  | 'Connecting'
  | 'Connected'
  | 'Reconnecting';

export type DxClusterStatus = {
  enabled: boolean;
  host: string;
  port: number;
  callsign: string;
  hasPassword: boolean;
  loginCommands: string;
  autoConnect: boolean;
  state: DxClusterConnectionState;
  spotsReceived: number;
  lastSpotCallsign: string | null;
  error: string | null;
};

export type DxClusterConfig = {
  enabled: boolean;
  host: string;
  port: number;
  callsign: string;
  password: string;
  loginCommands: string;
  autoConnect: boolean;
};

const STATES: DxClusterConnectionState[] = [
  'Disconnected',
  'Connecting',
  'Connected',
  'Reconnecting',
];

function normalizeState(raw: unknown): DxClusterConnectionState {
  if (typeof raw === 'string' && (STATES as string[]).includes(raw)) {
    return raw as DxClusterConnectionState;
  }
  // The enum may arrive numeric depending on serializer config.
  if (typeof raw === 'number' && raw >= 0 && raw < STATES.length) {
    return STATES[raw] ?? 'Disconnected';
  }
  return 'Disconnected';
}

export function normalizeStatus(raw: unknown): DxClusterStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' ? r.host : '',
    port: typeof r.port === 'number' ? r.port : 7373,
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    hasPassword: Boolean(r.hasPassword),
    loginCommands: typeof r.loginCommands === 'string' ? r.loginCommands : '',
    autoConnect: Boolean(r.autoConnect),
    state: normalizeState(r.state),
    spotsReceived: typeof r.spotsReceived === 'number' ? r.spotsReceived : 0,
    lastSpotCallsign:
      typeof r.lastSpotCallsign === 'string' && r.lastSpotCallsign.length > 0
        ? r.lastSpotCallsign
        : null,
    error: typeof r.error === 'string' && r.error.length > 0 ? r.error : null,
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

export function getDxClusterStatus(signal?: AbortSignal): Promise<DxClusterStatus> {
  return jsonFetch('/api/dxcluster/status', { signal }, normalizeStatus);
}

export function putDxClusterConfig(cfg: DxClusterConfig, signal?: AbortSignal): Promise<DxClusterStatus> {
  return jsonFetch(
    '/api/dxcluster/config',
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function connectDxCluster(signal?: AbortSignal): Promise<DxClusterStatus> {
  return jsonFetch('/api/dxcluster/connect', { method: 'POST', signal }, normalizeStatus);
}

export function disconnectDxCluster(signal?: AbortSignal): Promise<DxClusterStatus> {
  return jsonFetch('/api/dxcluster/disconnect', { method: 'POST', signal }, normalizeStatus);
}
