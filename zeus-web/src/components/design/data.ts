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

// Static data mirrored from the Claude Design handoff for Zeus SDR.
// These are design-time mocks for the panels that aren't yet backend-wired
// (QRZ lookup, logbook, memory channels, CW keyer macros).

export type BandSpec = {
  n: string;
  range: [number, number];
  center: number;
};

export const BANDS: BandSpec[] = [
  { n: '160', range: [1800000, 2000000], center: 1840000 },
  { n: '80', range: [3500000, 4000000], center: 3573000 },
  { n: '60', range: [5330500, 5405000], center: 5357000 },
  { n: '40', range: [7000000, 7300000], center: 7074000 },
  { n: '30', range: [10100000, 10150000], center: 10136000 },
  { n: '20', range: [14000000, 14350000], center: 14210000 },
  { n: '17', range: [18068000, 18168000], center: 18100000 },
  { n: '15', range: [21000000, 21450000], center: 21285000 },
  { n: '12', range: [24890000, 24990000], center: 24915000 },
  { n: '10', range: [28000000, 29700000], center: 28400000 },
  { n: '6', range: [50000000, 54000000], center: 50110000 },
];

export const MODES = ['LSB', 'USB', 'CW', 'AM', 'FM', 'DIGI'] as const;
export type Mode = (typeof MODES)[number];

export const AGC_VALUES = ['OFF', 'SLOW', 'MED', 'FAST'] as const;
export const FILTER_VALUES = [
  '50 Hz',
  '250 Hz',
  '500 Hz',
  '1.8 kHz',
  '2.4 kHz',
  '2.8 kHz',
  '6 kHz',
] as const;
export const POWER_VALUES = ['0.5W', '1W', '5W', '10W', '20W', '50W'] as const;

export function bandOf(hz: number): string {
  for (const b of BANDS) if (hz >= b.range[0] && hz <= b.range[1]) return b.n + 'm';
  return '—';
}

export type Contact = {
  callsign: string;
  name: string;
  location: string;
  grid: string;
  cq: string;
  itu: string;
  latlon: string;
  lat: number;
  lon: number;
  local: string;
  qsl: string;
  licensed: string;
  initials: string;
  flag: string;
  bearing: number;
  distance: number;
  age: number;
  class: string;
  rig: string;
  ant: string;
  power: string;
  qth: string;
  email: string;
  /** Optional QRZ.com portrait URL. Falls back to initials when absent. */
  photoUrl?: string;
  /** Optional deep link to the QRZ.com page for the click-through. */
  qrzUrl?: string;
  // ── Enrichment added with the QRZ-card upgrade (all optional) ───────────
  /** License codes string from QRZ (e.g. "HVE"), shown beside the class. */
  licenseCodes?: string | null;
  /** Years since the license effective date, when derivable. */
  licensedYears?: number | null;
  /** QSL acceptance flags from QRZ (null = unknown). */
  qslLotw?: boolean | null;
  qslEqsl?: boolean | null;
  qslMail?: boolean | null;
  qslManager?: string | null;
  /** Daylight state at the contact's QTH right now. */
  dayNight?: 'day' | 'grayline' | 'night' | null;
  /** Short distance unit summary, e.g. "5,920 km · 3,678 mi". */
  distanceLabel?: string | null;
};

export const CONTACTS: Record<string, Contact> = {
  EI6LF: {
    callsign: 'EI6LF',
    name: 'Brian',
    location: 'Ireland',
    grid: 'IO63VD',
    cq: '14',
    itu: '27',
    latlon: '53.35°N / 6.26°W',
    lat: 53.35,
    lon: -6.26,
    local: '18:27 IST',
    qsl: 'LoTW / eQSL',
    licensed: '2021',
    initials: 'BK',
    flag: '🇮🇪',
    bearing: 45,
    distance: 5920,
    age: 0,
    class: 'CEPT',
    rig: 'Hermes Lite 2',
    ant: 'EFHW @ 10m',
    power: '5W',
    qth: 'Dublin',
    email: 'ei6lf@qrz.com',
    photoUrl:
      'https://cdn-bio.qrz.com/f/ei6lf/me.png?p=f46e792eed883c99bf979d6301993341',
    qrzUrl: 'https://www.qrz.com/db/EI6LF',
  },
  IU2ABC: {
    callsign: 'IU2ABC',
    name: 'Marco Rossi',
    location: 'Milan, Italy',
    grid: 'JN45NK',
    cq: '15',
    itu: '28',
    latlon: '45.49°N / 9.16°E',
    lat: 45.49,
    lon: 9.16,
    local: '19:27 CEST',
    qsl: 'eQSL / Direct',
    licensed: '2014',
    initials: 'MR',
    flag: '🇮🇹',
    bearing: 42,
    distance: 7890,
    age: 38,
    class: 'Extra',
    rig: 'Icom IC-7300',
    ant: 'Hex Beam @ 12m',
    power: '100W',
    qth: 'Milan, Lombardy',
    email: 'iu2abc@arrl.net',
  },
  JA3XYZ: {
    callsign: 'JA3XYZ',
    name: 'Hiroshi Tanaka',
    location: 'Osaka, Japan',
    grid: 'PM74SO',
    cq: '25',
    itu: '45',
    latlon: '34.69°N / 135.50°E',
    lat: 34.69,
    lon: 135.5,
    local: '03:27 JST',
    qsl: 'LoTW',
    licensed: '2008',
    initials: 'HT',
    flag: '🇯🇵',
    bearing: 330,
    distance: 10280,
    age: 52,
    class: '1st Class',
    rig: 'Yaesu FTDX-101D',
    ant: '3-el Yagi @ 18m',
    power: '200W',
    qth: 'Osaka Pref.',
    email: 'ja3xyz@jarl.com',
  },
  VK4PQR: {
    callsign: 'VK4PQR',
    name: 'Dale Williams',
    location: 'Brisbane, AU',
    grid: 'QG62LI',
    cq: '30',
    itu: '55',
    latlon: '27.47°S / 153.02°E',
    lat: -27.47,
    lon: 153.02,
    local: '04:27 AEST',
    qsl: 'Direct',
    licensed: '1995',
    initials: 'DW',
    flag: '🇦🇺',
    bearing: 265,
    distance: 14450,
    age: 61,
    class: 'Advanced',
    rig: 'Elecraft K4',
    ant: 'SteppIR DB18E',
    power: '400W',
    qth: 'Brisbane QLD',
    email: 'vk4pqr@wia.org.au',
  },
  LU1DEF: {
    callsign: 'LU1DEF',
    name: 'Diego Fernández',
    location: 'Buenos Aires, AR',
    grid: 'GF05TE',
    cq: '13',
    itu: '14',
    latlon: '34.61°S / 58.38°W',
    lat: -34.61,
    lon: -58.38,
    local: '15:27 ART',
    qsl: 'LoTW / eQSL',
    licensed: '2001',
    initials: 'DF',
    flag: '🇦🇷',
    bearing: 165,
    distance: 8820,
    age: 44,
    class: 'General',
    rig: 'Kenwood TS-890',
    ant: 'Dipole @ 10m',
    power: '100W',
    qth: 'Buenos Aires',
    email: 'lu1def@lu.rc.ar',
  },
  K2LMN: {
    callsign: 'K2LMN',
    name: 'Rob Mitchell',
    location: 'Albany, NY',
    grid: 'FN32OU',
    cq: '05',
    itu: '08',
    latlon: '42.65°N / 73.76°W',
    lat: 42.65,
    lon: -73.76,
    local: '13:27 EDT',
    qsl: 'LoTW',
    licensed: '1988',
    initials: 'RM',
    flag: '🇺🇸',
    bearing: 58,
    distance: 1240,
    age: 67,
    class: 'Extra',
    rig: 'Flex 6600M',
    ant: '80m OCF Dipole',
    power: '100W',
    qth: 'Albany, NY',
    email: 'k2lmn@arrl.net',
  },
  EA3GHI: {
    callsign: 'EA3GHI',
    name: 'Carla Puig',
    location: 'Barcelona, Spain',
    grid: 'JN11CM',
    cq: '14',
    itu: '37',
    latlon: '41.39°N / 2.16°E',
    lat: 41.39,
    lon: 2.16,
    local: '20:27 CEST',
    qsl: 'LoTW',
    licensed: '2012',
    initials: 'CP',
    flag: '🇪🇸',
    bearing: 51,
    distance: 7310,
    age: 34,
    class: 'Class A',
    rig: 'Icom IC-7610',
    ant: 'Hex Beam',
    power: '100W',
    qth: 'Barcelona',
    email: 'ea3ghi@ure.es',
  },
};

export type LogEntry = {
  time: string;
  call: string;
  freq: string;
  mode: string;
  rst: string;
  name: string;
};

export const SAMPLE_LOG: LogEntry[] = [
  { time: '19:27', call: 'IU2ABC', freq: '14.210', mode: 'USB', rst: '59', name: 'Marco' },
  { time: '19:14', call: 'JA3XYZ', freq: '14.214', mode: 'USB', rst: '57', name: 'Hiro' },
  { time: '18:58', call: 'VK4PQR', freq: '21.285', mode: 'USB', rst: '55', name: 'Dale' },
  { time: '18:41', call: 'LU1DEF', freq: '14.205', mode: 'USB', rst: '59', name: 'Diego' },
  { time: '18:22', call: 'EA3GHI', freq: '7.074', mode: 'FT8', rst: '-12', name: 'Carla' },
  { time: '17:55', call: 'K2LMN', freq: '14.195', mode: 'USB', rst: '59', name: 'Rob' },
  { time: '17:31', call: 'DL9OPQ', freq: '3.573', mode: 'FT8', rst: '-08', name: 'Ute' },
];

export type MemoryChannel = {
  n: number;
  f: number;
  m: string;
  name: string;
};

export const MEMS: MemoryChannel[] = [
  { n: 1, f: 14.21, m: 'USB', name: 'DX Window' },
  { n: 2, f: 14.074, m: 'FT8', name: 'FT8 20m' },
  { n: 3, f: 7.074, m: 'FT8', name: 'FT8 40m' },
  { n: 4, f: 3.573, m: 'FT8', name: 'FT8 80m' },
  { n: 5, f: 14.1, m: 'CW', name: 'NCDXF Bcn' },
  { n: 6, f: 28.4, m: 'USB', name: '10m DX' },
  { n: 7, f: 144.2, m: 'USB', name: '2m SSB' },
  { n: 8, f: 50.11, m: 'USB', name: '6m DX' },
];
