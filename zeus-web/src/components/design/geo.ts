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

// Spherical geometry helpers — haversine distance, initial bearing, great-circle
// interpolation, and destination-point from bearing+range. Same math as
// Log4YM/src/Log4YM.Web/src/utils/maidenhead.ts and MapPlugin.tsx, rewritten
// here so Zeus stays free of cross-repo source dependencies.

const R_EARTH_KM = 6371;

const toRad = (deg: number) => (deg * Math.PI) / 180;
const toDeg = (rad: number) => (rad * 180) / Math.PI;

/**
 * Solar elevation angle (degrees above the horizon) at a point and instant.
 * Standard NOAA approximation — accurate to a fraction of a degree, plenty for
 * a day/night/grayline read on the QRZ card. Negative = sun below horizon.
 */
export function solarElevationDeg(
  lat: number,
  lon: number,
  date: Date = new Date(),
): number {
  const startOfYear = Date.UTC(date.getUTCFullYear(), 0, 0);
  const dayOfYear = (Date.UTC(
    date.getUTCFullYear(),
    date.getUTCMonth(),
    date.getUTCDate(),
  ) - startOfYear) / 86_400_000;
  const hoursUtc =
    date.getUTCHours() + date.getUTCMinutes() / 60 + date.getUTCSeconds() / 3600;

  // Fractional year (radians).
  const γ = ((2 * Math.PI) / 365) * (dayOfYear - 1 + (hoursUtc - 12) / 24);
  // Equation of time (minutes).
  const eqTime =
    229.18 *
    (0.000075 +
      0.001868 * Math.cos(γ) -
      0.032077 * Math.sin(γ) -
      0.014615 * Math.cos(2 * γ) -
      0.040849 * Math.sin(2 * γ));
  // Solar declination (radians).
  const decl =
    0.006918 -
    0.399912 * Math.cos(γ) +
    0.070257 * Math.sin(γ) -
    0.006758 * Math.cos(2 * γ) +
    0.000907 * Math.sin(2 * γ) -
    0.002697 * Math.cos(3 * γ) +
    0.00148 * Math.sin(3 * γ);

  const timeOffset = eqTime + 4 * lon; // lon east-positive, minutes
  const trueSolarTime = hoursUtc * 60 + timeOffset; // minutes
  const hourAngle = toRad(trueSolarTime / 4 - 180);

  const latR = toRad(lat);
  const cosZenith =
    Math.sin(latR) * Math.sin(decl) +
    Math.cos(latR) * Math.cos(decl) * Math.cos(hourAngle);
  const zenith = Math.acos(Math.max(-1, Math.min(1, cosZenith)));
  return 90 - toDeg(zenith);
}

export type DayNight = 'day' | 'grayline' | 'night';

/**
 * Classify a point's local lighting from solar elevation. "grayline" is the
 * twilight band around the terminator — the enhanced-DX window operators chase.
 */
export function dayNightAt(
  lat: number,
  lon: number,
  date: Date = new Date(),
): DayNight {
  const el = solarElevationDeg(lat, lon, date);
  if (el > 6) return 'day';
  if (el >= -6) return 'grayline';
  return 'night';
}

/** Haversine great-circle distance in kilometres. */
export function distanceKm(
  lat1: number,
  lon1: number,
  lat2: number,
  lon2: number,
): number {
  const φ1 = toRad(lat1);
  const φ2 = toRad(lat2);
  const Δφ = toRad(lat2 - lat1);
  const Δλ = toRad(lon2 - lon1);
  const a =
    Math.sin(Δφ / 2) ** 2 +
    Math.cos(φ1) * Math.cos(φ2) * Math.sin(Δλ / 2) ** 2;
  return 2 * R_EARTH_KM * Math.asin(Math.min(1, Math.sqrt(a)));
}

/** Initial bearing from (lat1,lon1) to (lat2,lon2) in degrees, 0 = N, CW. */
export function bearingDeg(
  lat1: number,
  lon1: number,
  lat2: number,
  lon2: number,
): number {
  const φ1 = toRad(lat1);
  const φ2 = toRad(lat2);
  const Δλ = toRad(lon2 - lon1);
  const y = Math.sin(Δλ) * Math.cos(φ2);
  const x =
    Math.cos(φ1) * Math.sin(φ2) - Math.sin(φ1) * Math.cos(φ2) * Math.cos(Δλ);
  return (toDeg(Math.atan2(y, x)) + 360) % 360;
}

/**
 * Destination point given start, bearing (deg CW from N), and range (km).
 * Vincenty-style forward formula on a sphere — good enough for HF beam hints.
 */
export function destinationPoint(
  lat: number,
  lon: number,
  bearingDeg_: number,
  rangeKm: number,
): [number, number] {
  const δ = rangeKm / R_EARTH_KM;
  const θ = toRad(bearingDeg_);
  const φ1 = toRad(lat);
  const λ1 = toRad(lon);
  const φ2 = Math.asin(
    Math.sin(φ1) * Math.cos(δ) + Math.cos(φ1) * Math.sin(δ) * Math.cos(θ),
  );
  const λ2 =
    λ1 +
    Math.atan2(
      Math.sin(θ) * Math.sin(δ) * Math.cos(φ1),
      Math.cos(δ) - Math.sin(φ1) * Math.sin(φ2),
    );
  return [toDeg(φ2), ((toDeg(λ2) + 540) % 360) - 180];
}

/**
 * Interpolate N points along the great-circle from a→b. Splits into multiple
 * polyline segments at the ±180° antimeridian so Leaflet doesn't draw a wrap
 * line across the whole world.
 */
export function greatCircleSegments(
  a: { lat: number; lon: number },
  b: { lat: number; lon: number },
  steps = 64,
): [number, number][][] {
  const φ1 = toRad(a.lat);
  const λ1 = toRad(a.lon);
  const φ2 = toRad(b.lat);
  const λ2 = toRad(b.lon);
  const Δφ = φ2 - φ1;
  const Δλ = λ2 - λ1;
  const aa =
    Math.sin(Δφ / 2) ** 2 + Math.cos(φ1) * Math.cos(φ2) * Math.sin(Δλ / 2) ** 2;
  const d = 2 * Math.atan2(Math.sqrt(aa), Math.sqrt(1 - aa));
  if (d === 0) return [[[a.lat, a.lon]]];

  const pts: [number, number][] = [];
  for (let i = 0; i <= steps; i++) {
    const f = i / steps;
    const A = Math.sin((1 - f) * d) / Math.sin(d);
    const B = Math.sin(f * d) / Math.sin(d);
    const x = A * Math.cos(φ1) * Math.cos(λ1) + B * Math.cos(φ2) * Math.cos(λ2);
    const y = A * Math.cos(φ1) * Math.sin(λ1) + B * Math.cos(φ2) * Math.sin(λ2);
    const z = A * Math.sin(φ1) + B * Math.sin(φ2);
    const φ = Math.atan2(z, Math.sqrt(x * x + y * y));
    const λ = Math.atan2(y, x);
    pts.push([toDeg(φ), toDeg(λ)]);
  }

  // Split at antimeridian crossings so Leaflet renders each segment cleanly.
  const segments: [number, number][][] = [[]];
  let prev: [number, number] | null = null;
  for (const p of pts) {
    if (prev && Math.abs(p[1] - prev[1]) > 180) segments.push([]);
    const seg = segments[segments.length - 1]!;
    seg.push(p);
    prev = p;
  }
  return segments.filter((s) => s.length > 1);
}

/**
 * Continuous great-circle path a→b as a single [lat, lon] list — longitudes
 * are unwrapped (may exceed ±180°) so consecutive points stay within 180° of
 * each other. Use for closed polygon fills where `greatCircleSegments`' split
 * would break the ring; Leaflet handles unwrapped longitudes when rendering.
 */
export function greatCirclePath(
  a: { lat: number; lon: number },
  b: { lat: number; lon: number },
  steps = 64,
): [number, number][] {
  const φ1 = toRad(a.lat);
  const λ1 = toRad(a.lon);
  const φ2 = toRad(b.lat);
  const λ2 = toRad(b.lon);
  const Δφ = φ2 - φ1;
  const Δλ = λ2 - λ1;
  const aa =
    Math.sin(Δφ / 2) ** 2 + Math.cos(φ1) * Math.cos(φ2) * Math.sin(Δλ / 2) ** 2;
  const d = 2 * Math.atan2(Math.sqrt(aa), Math.sqrt(1 - aa));
  if (d === 0) return [[a.lat, a.lon]];

  const pts: [number, number][] = [];
  let prevLon: number | null = null;
  for (let i = 0; i <= steps; i++) {
    const f = i / steps;
    const A = Math.sin((1 - f) * d) / Math.sin(d);
    const B = Math.sin(f * d) / Math.sin(d);
    const x = A * Math.cos(φ1) * Math.cos(λ1) + B * Math.cos(φ2) * Math.cos(λ2);
    const y = A * Math.cos(φ1) * Math.sin(λ1) + B * Math.cos(φ2) * Math.sin(λ2);
    const z = A * Math.sin(φ1) + B * Math.sin(φ2);
    const φ = Math.atan2(z, Math.sqrt(x * x + y * y));
    let lon = toDeg(Math.atan2(y, x));
    if (prevLon != null) {
      while (lon - prevLon > 180) lon -= 360;
      while (lon - prevLon < -180) lon += 360;
    }
    pts.push([toDeg(φ), lon]);
    prevLon = lon;
  }
  return pts;
}
