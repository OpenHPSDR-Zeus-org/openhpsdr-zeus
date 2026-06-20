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

// QRZ XML gives us a sparser record than the design-time Contact type; fill
// in the gaps so QrzCard renders the same whether the source is a panel
// lookup (App) or a click-through from the operator chat roster.

import type { QrzStation } from '../../api/qrz';
import type { Contact } from './data';
import { bearingDeg, distanceKm, dayNightAt } from './geo';

function licenseYear(efdate: string | null): number | null {
  if (!efdate) return null;
  const m = /(\d{4})/.exec(efdate);
  if (!m) return null;
  const y = Number(m[1]);
  return y >= 1900 && y <= 2100 ? y : null;
}

/** Contact's wall-clock time from the QRZ GMT offset, e.g. "03:14". */
function localTimeFromOffset(gmtOffset: number | null, tz: string | null): string {
  if (gmtOffset == null) return '—';
  const utcMs = Date.now() + new Date().getTimezoneOffset() * 60_000;
  const d = new Date(utcMs + gmtOffset * 3_600_000);
  const hh = String(d.getHours()).padStart(2, '0');
  const mm = String(d.getMinutes()).padStart(2, '0');
  return tz ? `${hh}:${mm} ${tz}` : `${hh}:${mm}`;
}

/** "LoTW · eQSL · Direct" summary, or "—" when nothing is known. */
function qslSummary(s: QrzStation): string {
  const parts: string[] = [];
  if (s.acceptsLotw) parts.push('LoTW');
  if (s.acceptsEqsl) parts.push('eQSL');
  if (s.acceptsMailQsl) parts.push('Direct');
  if (s.qslManager) parts.push(`via ${s.qslManager}`);
  return parts.length ? parts.join(' · ') : '—';
}

export function qrzStationToContact(s: QrzStation | null, home: QrzStation | null): Contact | null {
  if (!s || s.lat == null || s.lon == null) return null;
  const bearing = home?.lat != null && home?.lon != null
    ? bearingDeg(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const distance = home?.lat != null && home?.lon != null
    ? distanceKm(home.lat, home.lon, s.lat, s.lon)
    : 0;
  const first = (s.firstName || s.name || '').trim().charAt(0).toUpperCase();
  const last = (s.name || '').trim().split(/\s+/).pop()?.charAt(0).toUpperCase() ?? '';
  const initials = (first + last) || s.callsign.slice(0, 2);
  const location = [s.city, s.state, s.country].filter(Boolean).join(', ') || (s.country ?? '—');
  const fullName = [s.firstName, s.name].filter(Boolean).join(' ') || '—';

  const licYear = licenseYear(s.licenseEffectiveDate);
  const licensedYears = licYear != null ? new Date().getFullYear() - licYear : null;
  const distanceMi = distance * 0.621371;
  const distanceLabel = distance > 0
    ? `${Math.round(distance).toLocaleString()} km · ${Math.round(distanceMi).toLocaleString()} mi`
    : null;

  return {
    callsign: s.callsign,
    name: fullName,
    location,
    grid: s.grid ?? '—',
    cq: s.cqZone != null ? String(s.cqZone).padStart(2, '0') : '—',
    itu: s.ituZone != null ? String(s.ituZone).padStart(2, '0') : '—',
    latlon: `${Math.abs(s.lat).toFixed(2)}°${s.lat >= 0 ? 'N' : 'S'} / ${Math.abs(s.lon).toFixed(2)}°${s.lon >= 0 ? 'E' : 'W'}`,
    lat: s.lat,
    lon: s.lon,
    local: localTimeFromOffset(s.gmtOffset, s.timeZone),
    qsl: qslSummary(s),
    licensed: licYear != null ? String(licYear) : '—',
    initials,
    flag: '',
    bearing,
    distance,
    age: 0,
    class: s.licenseClass ?? '—',
    rig: '—',
    ant: '—',
    power: '—',
    qth: s.city ?? s.country ?? '—',
    email: s.email ?? '—',
    photoUrl: s.imageUrl ?? undefined,
    qrzUrl: `https://www.qrz.com/db/${s.callsign}`,
    // Enrichment
    licenseCodes: s.licenseCodes,
    licensedYears,
    qslLotw: s.acceptsLotw,
    qslEqsl: s.acceptsEqsl,
    qslMail: s.acceptsMailQsl,
    qslManager: s.qslManager,
    dayNight: dayNightAt(s.lat, s.lon),
    distanceLabel,
  };
}
