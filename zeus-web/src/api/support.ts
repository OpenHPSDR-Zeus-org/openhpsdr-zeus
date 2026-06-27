// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

// Operator-side client for the read-only maintainer "support session" surface
// (remote-diag P5). These endpoints are intentionally LOCAL-only at the radio —
// the support/operator tunnels exclude /api/support, so a remote peer can never
// approve a session for itself. The master switch (availability) defaults OFF.

export type SupportAvailability = {
  available: boolean;
  autoShareCrashes: boolean;
};

export type PendingSupportRequest = {
  requestId: string;
  adminCallsign: string;
  createdAt: string;
  expiresAt: string;
};

export type SupportStatus = {
  available: boolean;
  autoShareCrashes: boolean;
  pending: PendingSupportRequest[];
  activeSessions: number;
};

export class SupportApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'SupportApiError';
  }
}

async function jsonFetch<T>(input: RequestInfo, init?: RequestInit): Promise<T> {
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
      /* non-JSON body — keep status text */
    }
    throw new SupportApiError(res.status, message);
  }
  return (await res.json()) as T;
}

function normalizePending(raw: unknown): PendingSupportRequest[] {
  if (!Array.isArray(raw)) return [];
  const out: PendingSupportRequest[] = [];
  for (const r of raw) {
    if (!r || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const requestId = typeof o.requestId === 'string' ? o.requestId : '';
    if (!requestId) continue;
    out.push({
      requestId,
      adminCallsign: typeof o.adminCallsign === 'string' ? o.adminCallsign : '',
      createdAt: typeof o.createdAt === 'string' ? o.createdAt : '',
      expiresAt: typeof o.expiresAt === 'string' ? o.expiresAt : '',
    });
  }
  return out;
}

function normalizeStatus(raw: unknown): SupportStatus {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    available: o.available === true,
    autoShareCrashes: o.autoShareCrashes === true,
    pending: normalizePending(o.pending),
    activeSessions: typeof o.activeSessions === 'number' ? o.activeSessions : 0,
  };
}

export async function getSupportAvailability(signal?: AbortSignal): Promise<SupportAvailability> {
  const o = await jsonFetch<Record<string, unknown>>('/api/support/availability', { signal });
  return {
    available: o.available === true,
    autoShareCrashes: o.autoShareCrashes === true,
  };
}

export async function setSupportAvailability(
  body: SupportAvailability,
  signal?: AbortSignal,
): Promise<SupportAvailability> {
  const o = await jsonFetch<Record<string, unknown>>('/api/support/availability', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });
  return {
    available: o.available === true,
    autoShareCrashes: o.autoShareCrashes === true,
  };
}

export async function getSupportStatus(signal?: AbortSignal): Promise<SupportStatus> {
  return normalizeStatus(await jsonFetch<unknown>('/api/support/status', { signal }));
}

export async function approveSupportRequest(requestId: string, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch('/api/support/approve', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ requestId }),
    signal,
  });
  return res.ok;
}

export async function denySupportRequest(requestId: string, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch('/api/support/deny', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ requestId }),
    signal,
  });
  return res.ok;
}
