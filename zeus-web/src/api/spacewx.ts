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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Comprehensive solar / space-weather snapshot for the dashboard panel. The
// Zeus backend proxies the embedded HamClock sidecar's N0NBH feed; see
// SpaceWeatherService.cs. When the sidecar isn't running the endpoint returns
// { available: false, unavailable: <reason> } rather than erroring.

export type SpaceWeatherBand = {
  name: string;
  time: string; // "day" | "night"
  condition: string;
};

export type SpaceWeatherVhf = {
  name: string;
  location: string;
  condition: string;
};

export type SpaceWeatherSnapshot = {
  available: boolean;
  unavailable: string | null;
  source: string | null;
  updated: string | null;
  fetchedAt: number | null;
  solarFlux: string | null;
  sunspots: string | null;
  aIndex: string | null;
  kIndex: string | null;
  kIndexNt: string | null;
  xray: string | null;
  heliumLine: string | null;
  protonFlux: string | null;
  electronFlux: string | null;
  aurora: string | null;
  normalization: string | null;
  latDegree: string | null;
  solarWind: string | null;
  magneticField: string | null;
  fof2: string | null;
  mufFactor: string | null;
  muf: string | null;
  geomagField: string | null;
  signalNoise: string | null;
  bandConditions: SpaceWeatherBand[];
  vhfConditions: SpaceWeatherVhf[];
};

function str(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function num(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function normBand(raw: unknown): SpaceWeatherBand {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    name: typeof r.name === 'string' ? r.name : '',
    time: typeof r.time === 'string' ? r.time : '',
    condition: typeof r.condition === 'string' ? r.condition : '',
  };
}

function normVhf(raw: unknown): SpaceWeatherVhf {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    name: typeof r.name === 'string' ? r.name : '',
    location: typeof r.location === 'string' ? r.location : '',
    condition: typeof r.condition === 'string' ? r.condition : '',
  };
}

function normalize(raw: unknown): SpaceWeatherSnapshot {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    available: Boolean(r.available),
    unavailable: str(r.unavailable),
    source: str(r.source),
    updated: str(r.updated),
    fetchedAt: num(r.fetchedAt),
    solarFlux: str(r.solarFlux),
    sunspots: str(r.sunspots),
    aIndex: str(r.aIndex),
    kIndex: str(r.kIndex),
    kIndexNt: str(r.kIndexNt),
    xray: str(r.xray),
    heliumLine: str(r.heliumLine),
    protonFlux: str(r.protonFlux),
    electronFlux: str(r.electronFlux),
    aurora: str(r.aurora),
    normalization: str(r.normalization),
    latDegree: str(r.latDegree),
    solarWind: str(r.solarWind),
    magneticField: str(r.magneticField),
    fof2: str(r.fof2),
    mufFactor: str(r.mufFactor),
    muf: str(r.muf),
    geomagField: str(r.geomagField),
    signalNoise: str(r.signalNoise),
    bandConditions: Array.isArray(r.bandConditions) ? r.bandConditions.map(normBand) : [],
    vhfConditions: Array.isArray(r.vhfConditions) ? r.vhfConditions.map(normVhf) : [],
  };
}

export async function fetchSpaceWeather(signal?: AbortSignal): Promise<SpaceWeatherSnapshot> {
  const res = await fetch('/api/spacewx', { signal });
  if (!res.ok) {
    return normalize({ available: false, unavailable: `HTTP ${res.status}` });
  }
  return normalize((await res.json()) as unknown);
}
