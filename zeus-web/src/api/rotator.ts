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
  activeSlotId: number;
  slotCount: number;
};

export type RotctldConfig = {
  enabled: boolean;
  host: string;
  port: number;
  pollingIntervalMs: number;
};

export type RotctldSlot = {
  id: number;
  label: string;
  enabled: boolean;
  host: string;
  port: number;
  bands: string[];
  pollingIntervalMs: number;
};

export type RotctldMultiConfig = {
  activeSlotId: number;
  autoRoute: boolean;
  slots: RotctldSlot[];
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
    activeSlotId: typeof r.activeSlotId === 'number' && r.activeSlotId > 0 ? r.activeSlotId : 1,
    slotCount: typeof r.slotCount === 'number' && r.slotCount > 0 ? r.slotCount : 1,
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

function normalizeSlot(raw: unknown): RotctldSlot {
  const r = (raw ?? {}) as Record<string, unknown>;
  const bandsRaw = Array.isArray(r.bands) ? r.bands : [];
  return {
    id: typeof r.id === 'number' && r.id > 0 ? r.id : 1,
    label: typeof r.label === 'string' && r.label ? r.label : 'Rotator',
    enabled: Boolean(r.enabled),
    host: typeof r.host === 'string' && r.host ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' && r.port > 0 ? r.port : 4533,
    bands: bandsRaw.filter((b): b is string => typeof b === 'string'),
    pollingIntervalMs:
      typeof r.pollingIntervalMs === 'number' && r.pollingIntervalMs > 0
        ? r.pollingIntervalMs
        : 500,
  };
}

function normalizeMultiConfig(raw: unknown): RotctldMultiConfig {
  const r = (raw ?? {}) as Record<string, unknown>;
  const slotsRaw = Array.isArray(r.slots) ? r.slots : [];
  const slots = slotsRaw.map(normalizeSlot);
  const fallbackActive = slots[0]?.id ?? 1;
  return {
    activeSlotId:
      typeof r.activeSlotId === 'number' && slots.some((s) => s.id === r.activeSlotId)
        ? (r.activeSlotId as number)
        : fallbackActive,
    autoRoute: Boolean(r.autoRoute),
    slots,
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

export function getRotatorMultiConfig(signal?: AbortSignal): Promise<RotctldMultiConfig> {
  return jsonFetch('/api/rotator/multi-config', { signal }, normalizeMultiConfig);
}

export function postRotatorMultiConfig(cfg: RotctldMultiConfig, signal?: AbortSignal): Promise<RotctldMultiConfig> {
  return jsonFetch(
    '/api/rotator/multi-config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeMultiConfig,
  );
}

export function setRotatorActiveSlot(slotId: number, signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch(
    '/api/rotator/active',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ slotId }),
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
