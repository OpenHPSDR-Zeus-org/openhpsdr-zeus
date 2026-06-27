// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the N1MM-format ("N1MM Logger+ Broadcasts") UDP broadcaster.
// This is the contactinfo datagram HRD Logbook QSO-Forwarding and the
// DXKeeper-via-Gateway path expect — a DIFFERENT wire format from the WSJT-X
// type-12 ADIF datagram, on its own configurable port (default 2333). SEND-ONLY,
// egress OFF by default, applies live (no listener, no restart). These records
// live server-side only (not in Zeus.Contracts) — no wire-format change.

import { ApiError } from './client';

export type N1mmConfig = {
  enabled: boolean;
  host: string;
  port: number;
};

export const N1MM_DEFAULT_PORT = 2333;

function normalizeConfig(raw: unknown): N1mmConfig {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' ? r.port : N1MM_DEFAULT_PORT,
  };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  return parse((await res.json()) as unknown);
}

export function getN1mmConfig(signal?: AbortSignal): Promise<N1mmConfig> {
  return jsonFetch('/api/n1mm/config', { signal }, normalizeConfig);
}

export function postN1mmConfig(cfg: N1mmConfig, signal?: AbortSignal): Promise<N1mmConfig> {
  return jsonFetch(
    '/api/n1mm/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeConfig,
  );
}
