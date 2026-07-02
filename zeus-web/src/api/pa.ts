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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

export type PaBandSettings = {
  band: string;
  paGainDb: number;
  disablePa: boolean;
  ocTx: number;
  ocRx: number;
  // Read-only firmware auto-mask (N2ADR LPF on HL2, 0 elsewhere). Server
  // recomputes from the connected board on every GET; the PUT path ignores
  // it. Used by PaSettingsPanel to surface which pins the firmware is
  // already driving for each band.
  autoOcMask: number;
  // Anvelina-PRO3 DX OC masks (issue Kb2uka/openhpsdr-zeus#407) — 4-bit
  // masks for USEROUT7..10 (bit 0 = DX OUT 7, bit 1 = DX OUT 8,
  // bit 2 = DX OUT 9, bit 3 = DX OUT 10). Honoured by the wire path only
  // when the connected radio is Anvelina-PRO3 over Protocol 2 (see
  // BoardCapabilities.supportsAnvelinaDxOc). Persisted on every band so
  // DX wiring travels with the band selection.
  ocDxTx: number;
  ocDxRx: number;
  // Per-band Drive%/Tune% recall + lock (issue #1279). null = never set;
  // band-crossing will carry the current global value forward on first
  // visit. Locked = the recalled value is stable — the operator can still
  // drag the slider, but the write-back to the per-band entry is skipped
  // until they unlock, so the next band cross recalls the locked value.
  drivePct: number | null;
  tunePct: number | null;
  driveLocked: boolean;
  tuneLocked: boolean;
};

export type PaGlobalSettings = {
  paEnabled: boolean;
  paMaxPowerWatts: number;
};

export type PaSettings = {
  global: PaGlobalSettings;
  bands: PaBandSettings[];
};

type PaBandDtoRaw = {
  band?: unknown;
  paGainDb?: unknown;
  disablePa?: unknown;
  ocTx?: unknown;
  ocRx?: unknown;
  autoOcMask?: unknown;
  ocDxTx?: unknown;
  ocDxRx?: unknown;
  drivePct?: unknown;
  tunePct?: unknown;
  driveLocked?: unknown;
  tuneLocked?: unknown;
};

type PaGlobalDtoRaw = {
  paEnabled?: unknown;
  paMaxPowerWatts?: unknown;
};

type PaSettingsDtoRaw = {
  global?: PaGlobalDtoRaw;
  bands?: unknown;
};

function toNumber(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

function toBool(v: unknown, fallback = false): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function normalizeGlobal(raw: PaGlobalDtoRaw | undefined): PaGlobalSettings {
  return {
    paEnabled: toBool(raw?.paEnabled, true),
    paMaxPowerWatts: Math.max(0, Math.round(toNumber(raw?.paMaxPowerWatts, 0))),
  };
}

function toPercentOrNull(v: unknown): number | null {
  if (typeof v !== 'number' || !Number.isFinite(v)) return null;
  return Math.max(0, Math.min(100, Math.round(v)));
}

function normalizeBand(raw: PaBandDtoRaw): PaBandSettings {
  return {
    band: typeof raw.band === 'string' ? raw.band : '',
    paGainDb: toNumber(raw.paGainDb, 0),
    disablePa: toBool(raw.disablePa, false),
    ocTx: Math.max(0, Math.min(0x7f, Math.round(toNumber(raw.ocTx, 0)))),
    ocRx: Math.max(0, Math.min(0x7f, Math.round(toNumber(raw.ocRx, 0)))),
    autoOcMask: Math.max(0, Math.min(0x7f, Math.round(toNumber(raw.autoOcMask, 0)))),
    // DX masks are 4-bit per the EU2AV Anvelina spec (#407) — bits [4:1]
    // on the wire. Clamp to 0x0F so a malformed server response can't
    // smuggle bits the wire path would silently zero anyway.
    ocDxTx: Math.max(0, Math.min(0x0f, Math.round(toNumber(raw.ocDxTx, 0)))),
    ocDxRx: Math.max(0, Math.min(0x0f, Math.round(toNumber(raw.ocDxRx, 0)))),
    drivePct: toPercentOrNull(raw.drivePct),
    tunePct: toPercentOrNull(raw.tunePct),
    driveLocked: toBool(raw.driveLocked, false),
    tuneLocked: toBool(raw.tuneLocked, false),
  };
}

function normalize(raw: PaSettingsDtoRaw): PaSettings {
  const bandsArr = Array.isArray(raw.bands) ? (raw.bands as PaBandDtoRaw[]) : [];
  return {
    global: normalizeGlobal(raw.global),
    bands: bandsArr.filter((b) => typeof b?.band === 'string').map(normalizeBand),
  };
}

// boardOverride=undefined → use the effective board (connected > preferred).
// Passing a board name lets the radio-selector preview another board's
// defaults for empty rows without mutating the stored preference. Existing
// saved per-band calibration is unaffected either way.
export async function fetchPaSettings(
  signal?: AbortSignal,
  boardOverride?: string,
): Promise<PaSettings> {
  const url = boardOverride
    ? `/api/pa-settings?board=${encodeURIComponent(boardOverride)}`
    : '/api/pa-settings';
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`GET ${url} → ${res.status}`);
  const raw = (await res.json()) as PaSettingsDtoRaw;
  return normalize(raw);
}

// Pure board defaults — hits /api/pa-settings/defaults which skips the
// LiteDB pa_bands collection entirely and returns the piHPSDR/Thetis seed
// values for the requested board. Used by the "Reset to defaults" button
// to stomp prior calibration.
export async function fetchPaDefaults(
  boardOverride?: string,
  signal?: AbortSignal,
): Promise<PaSettings> {
  const url = boardOverride
    ? `/api/pa-settings/defaults?board=${encodeURIComponent(boardOverride)}`
    : '/api/pa-settings/defaults';
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`GET ${url} → ${res.status}`);
  const raw = (await res.json()) as PaSettingsDtoRaw;
  return normalize(raw);
}

export async function updatePaSettings(
  settings: PaSettings,
  signal?: AbortSignal,
): Promise<PaSettings> {
  const res = await fetch('/api/pa-settings', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(settings),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/pa-settings → ${res.status}`);
  const raw = (await res.json()) as PaSettingsDtoRaw;
  return normalize(raw);
}

// Per-band Drive%/Tune% lock toggle (issue #1279). Either flag can be omitted
// to leave the other untouched. Server returns the whole PA snapshot so the
// pa-store picks up the new lock state without a follow-up GET.
export async function updatePaBandLocks(
  band: string,
  locks: { driveLocked?: boolean; tuneLocked?: boolean },
  signal?: AbortSignal,
): Promise<PaSettings> {
  const url = `/api/pa-settings/band/${encodeURIComponent(band)}/locks`;
  const res = await fetch(url, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(locks),
    signal,
  });
  if (!res.ok) throw new Error(`PUT ${url} → ${res.status}`);
  const raw = (await res.json()) as PaSettingsDtoRaw;
  return normalize(raw);
}
