// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the per-QSO HTTP cloud-log uploaders (Wavelog/Cloudlog +
// Club Log realtime). Each logged QSO is pushed as a single ADIF record. Egress
// is OFF by default and SEND-ONLY. SECRETS ARE WRITE-ONLY: the config/status
// shape only ever reports secret PRESENCE booleans (hasApiKey / hasPassword);
// the keys themselves go up through POST /api/log/cloud/credentials and never
// come back down. These records live server-side only (not in Zeus.Contracts).

import { ApiError } from './client';

export type WavelogStatus = {
  enabled: boolean;
  baseUrl: string;
  stationProfileId: string;
  hasApiKey: boolean;
};

export type ClubLogStatus = {
  enabled: boolean;
  email: string;
  callsign: string;
  hasPassword: boolean;
  hasApiKey: boolean;
};

export type CloudLogStatus = {
  wavelog: WavelogStatus;
  clubLog: ClubLogStatus;
};

// Non-secret config written back to the server. Mirrors CloudLogConfig server-side.
export type CloudLogConfig = {
  wavelog: { enabled: boolean; baseUrl: string; stationProfileId: string };
  clubLog: { enabled: boolean; email: string; callsign: string };
};

// Write-only secret setter. Only include the fields being changed; an empty
// string clears that secret, a field left out is unchanged on the server.
export type CloudLogCredentials = {
  wavelogApiKey?: string;
  clubLogPassword?: string;
  clubLogApiKey?: string;
};

function normalizeStatus(raw: unknown): CloudLogStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  const wl = (r.wavelog ?? {}) as Record<string, unknown>;
  const cl = (r.clubLog ?? {}) as Record<string, unknown>;
  return {
    wavelog: {
      enabled: Boolean(wl.enabled),
      baseUrl: typeof wl.baseUrl === 'string' ? wl.baseUrl : '',
      stationProfileId: typeof wl.stationProfileId === 'string' ? wl.stationProfileId : '',
      hasApiKey: Boolean(wl.hasApiKey),
    },
    clubLog: {
      enabled: Boolean(cl.enabled),
      email: typeof cl.email === 'string' ? cl.email : '',
      callsign: typeof cl.callsign === 'string' ? cl.callsign : '',
      hasPassword: Boolean(cl.hasPassword),
      hasApiKey: Boolean(cl.hasApiKey),
    },
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

export function getCloudLogStatus(signal?: AbortSignal): Promise<CloudLogStatus> {
  return jsonFetch('/api/log/cloud/config', { signal }, normalizeStatus);
}

export function postCloudLogConfig(cfg: CloudLogConfig, signal?: AbortSignal): Promise<CloudLogStatus> {
  return jsonFetch(
    '/api/log/cloud/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function postCloudLogCredentials(
  creds: CloudLogCredentials,
  signal?: AbortSignal,
): Promise<CloudLogStatus> {
  return jsonFetch(
    '/api/log/cloud/credentials',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(creds),
      signal,
    },
    normalizeStatus,
  );
}
