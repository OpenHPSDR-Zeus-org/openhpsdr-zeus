// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

import { ApiError } from './client';

export type QrzStation = {
  callsign: string;
  name: string | null;
  firstName: string | null;
  country: string | null;
  state: string | null;
  city: string | null;
  grid: string | null;
  lat: number | null;
  lon: number | null;
  dxcc: number | null;
  cqZone: number | null;
  ituZone: number | null;
  imageUrl: string | null;
  // ── Extended fields (added alongside the QRZ-card enrichment work) ──────
  licenseClass: string | null;
  licenseCodes: string | null;
  /** Current-grant effective date (yyyy-MM-dd). Not necessarily first-licensed. */
  licenseEffectiveDate: string | null;
  licenseExpiresDate: string | null;
  email: string | null;
  acceptsLotw: boolean | null;
  acceptsEqsl: boolean | null;
  acceptsMailQsl: boolean | null;
  qslManager: string | null;
  /** Hours offset from UTC for the contact's QTH (QRZ GMTOffset). */
  gmtOffset: number | null;
  timeZone: string | null;
  observesDst: boolean | null;
  born: number | null;
};

export type QrzStatus = {
  connected: boolean;
  hasXmlSubscription: boolean;
  home: QrzStation | null;
  error: string | null;
  hasApiKey: boolean;
  // True when the backend has username+password persisted in its credential
  // store. The backend uses these to silently re-login on startup, so this
  // flag tells the frontend "if connected is false but I'm trying" — useful
  // for the on-load retry probe in qrz-store.ts.
  hasStoredCredentials: boolean;
};

function toNum(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function toStr(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function toBool(v: unknown): boolean | null {
  return typeof v === 'boolean' ? v : null;
}

function normalizeStation(raw: unknown): QrzStation {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    name: toStr(r.name),
    firstName: toStr(r.firstName),
    country: toStr(r.country),
    state: toStr(r.state),
    city: toStr(r.city),
    grid: toStr(r.grid),
    lat: toNum(r.lat),
    lon: toNum(r.lon),
    dxcc: toNum(r.dxcc),
    cqZone: toNum(r.cqZone),
    ituZone: toNum(r.ituZone),
    imageUrl: toStr(r.imageUrl),
    licenseClass: toStr(r.licenseClass),
    licenseCodes: toStr(r.licenseCodes),
    licenseEffectiveDate: toStr(r.licenseEffectiveDate),
    licenseExpiresDate: toStr(r.licenseExpiresDate),
    email: toStr(r.email),
    acceptsLotw: toBool(r.acceptsLotw),
    acceptsEqsl: toBool(r.acceptsEqsl),
    acceptsMailQsl: toBool(r.acceptsMailQsl),
    qslManager: toStr(r.qslManager),
    gmtOffset: toNum(r.gmtOffset),
    timeZone: toStr(r.timeZone),
    observesDst: toBool(r.observesDst),
    born: toNum(r.born),
  };
}

function normalizeStatus(raw: unknown): QrzStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    connected: Boolean(r.connected),
    hasXmlSubscription: Boolean(r.hasXmlSubscription),
    home: r.home ? normalizeStation(r.home) : null,
    error: toStr(r.error),
    hasApiKey: Boolean(r.hasApiKey),
    hasStoredCredentials: Boolean(r.hasStoredCredentials),
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

export function qrzStatus(signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch('/api/qrz/status', { signal }, normalizeStatus);
}

export function qrzLogin(username: string, password: string, signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch(
    '/api/qrz/login',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ username, password }),
      signal,
    },
    normalizeStatus,
  );
}

export function qrzLookup(callsign: string, signal?: AbortSignal): Promise<QrzStation> {
  return jsonFetch(
    '/api/qrz/lookup',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ callsign }),
      signal,
    },
    normalizeStation,
  );
}

export function qrzLogout(signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch('/api/qrz/logout', { method: 'POST', signal }, normalizeStatus);
}

export function qrzSetApiKey(apiKey: string | null, signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch(
    '/api/qrz/apikey',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ apiKey }),
      signal,
    },
    normalizeStatus,
  );
}
