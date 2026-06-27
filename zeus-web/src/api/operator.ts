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
// REST client for the SHARED operator identity (callsign + Maidenhead grid).
// This is the SAME identity the spotting uploaders and FreeDV Reporter resolve
// from — set it once and FT8/FT4 TX ungates everywhere. Server-persisted
// (zeus-prefs.db) so the desktop webview no longer loses it on every restart
// (its old localStorage was scoped to a port the OS reassigns each launch).
//
// GET returns both the saved override AND the effective resolved identity
// (override first, QRZ home fallback) so the FT8 Settings page can show the live
// values greyed when they fall back to QRZ.

import { ApiError } from './client';

/** The operator's saved override (what they typed in Settings). */
export type OperatorIdentity = {
  callsign: string;
  grid: string;
};

/** GET/POST /api/operator response — override + effective resolved identity. */
export type OperatorIdentityStatus = {
  /** Saved override (empty when unset). */
  callsign: string;
  grid: string;
  /** Effective value: override else QRZ home. */
  resolvedCallsign: string;
  resolvedGrid: string;
  /** True when the resolved field fell back to the QRZ home station. */
  callsignFromQrz: boolean;
  gridFromQrz: boolean;
  /** True when a callsign + grid are available from any source. */
  identityResolved: boolean;
};

function normalizeStatus(raw: unknown): OperatorIdentityStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  const str = (v: unknown) => (typeof v === 'string' ? v : '');
  return {
    callsign: str(r.callsign),
    grid: str(r.grid),
    resolvedCallsign: str(r.resolvedCallsign),
    resolvedGrid: str(r.resolvedGrid),
    callsignFromQrz: Boolean(r.callsignFromQrz),
    gridFromQrz: Boolean(r.gridFromQrz),
    identityResolved: Boolean(r.identityResolved),
  };
}

async function jsonFetch(
  input: RequestInfo,
  init: RequestInit | undefined,
): Promise<OperatorIdentityStatus> {
  const res = await fetch(input, init);
  if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  return normalizeStatus((await res.json()) as unknown);
}

export function getOperator(signal?: AbortSignal): Promise<OperatorIdentityStatus> {
  return jsonFetch('/api/operator', { signal });
}

export function postOperator(
  identity: OperatorIdentity,
  signal?: AbortSignal,
): Promise<OperatorIdentityStatus> {
  return jsonFetch('/api/operator', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(identity),
    signal,
  });
}
