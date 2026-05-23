// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

export type RotctldStatus = {
  enabled: boolean;
  connected: boolean;
  host: string;
  port: number;
  currentAz: number | null;
  targetAz: number | null;
  moving: boolean;
  error: string | null;
};

export type RotctldConfig = {
  enabled: boolean;
  host: string;
  port: number;
  pollingIntervalMs: number;
};

export type RotctldTestResult = { ok: boolean; error: string | null };

function toNum(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function normalizeStatus(raw: unknown): RotctldStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    connected: Boolean(r.connected),
    host: typeof r.host === 'string' ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' ? r.port : 4533,
    currentAz: toNum(r.currentAz),
    targetAz: toNum(r.targetAz),
    moving: Boolean(r.moving),
    error: typeof r.error === 'string' && r.error.length > 0 ? r.error : null,
  };
}

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok && res.status !== 503) {
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
  // 503 carries a body with error set — let the caller inspect the normalized status.
  return parse((await res.json()) as unknown);
}

export function getRotatorStatus(signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch('/api/rotator/status', { signal }, normalizeStatus);
}

function normalizeConfig(raw: unknown): RotctldConfig {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' && r.host ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' && r.port > 0 ? r.port : 4533,
    pollingIntervalMs:
      typeof r.pollingIntervalMs === 'number' && r.pollingIntervalMs > 0
        ? r.pollingIntervalMs
        : 500,
  };
}

export function getRotatorConfig(signal?: AbortSignal): Promise<RotctldConfig> {
  return jsonFetch('/api/rotator/config', { signal }, normalizeConfig);
}

export function postRotatorConfig(cfg: RotctldConfig, signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch(
    '/api/rotator/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function setRotatorAz(azimuth: number, signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch(
    '/api/rotator/set',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ azimuth }),
      signal,
    },
    normalizeStatus,
  );
}

export function stopRotator(signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch('/api/rotator/stop', { method: 'POST', signal }, normalizeStatus);
}

export function testRotator(host: string, port: number, signal?: AbortSignal): Promise<RotctldTestResult> {
  return jsonFetch(
    '/api/rotator/test',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ host, port }),
      signal,
    },
    (raw) => {
      const r = (raw ?? {}) as Record<string, unknown>;
      return { ok: Boolean(r.ok), error: typeof r.error === 'string' && r.error ? r.error : null };
    },
  );
}
