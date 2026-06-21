// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Analog S-Meter — shared geometry, scale defs, and needle ballistics.
// Translated from the design handoff (display/project/s-meter-shared.jsx).
// Pure functions / data; no React. Imported by AnalogMeterFace and the panel.

// The face is drawn into a 1000×420 SVG. Pivot sits below the visible bottom
// edge so the arc reads as a wide, shallow segment of a circle — like a real
// moving-coil meter.
export const FACE = {
  w: 1000,
  h: 420,
  cx: 500,
  cy: 760,
  rOuter: 700,
  arcGap: 50,
  sweep: 70,
} as const;

export function pt(cx: number, cy: number, r: number, deg: number): [number, number] {
  const a = ((deg - 90) * Math.PI) / 180;
  return [cx + r * Math.cos(a), cy + r * Math.sin(a)];
}

export function arcPath(cx: number, cy: number, r: number, d0: number, d1: number): string {
  const [x0, y0] = pt(cx, cy, r, d0);
  const [x1, y1] = pt(cx, cy, r, d1);
  const large = Math.abs(d1 - d0) > 180 ? 1 : 0;
  const sweepFlag = d1 > d0 ? 1 : 0;
  return `M ${x0} ${y0} A ${r} ${r} 0 ${large} ${sweepFlag} ${x1} ${y1}`;
}

export function normToDeg(n: number): number {
  const half = FACE.sweep / 2;
  return -half + n * FACE.sweep;
}

export type ScaleId = 's' | 'po' | 'swr';

export interface ScaleTick {
  v: number;
  label: string;
  major: boolean;
  /** S-meter +dB region (S9+20/+40/+60). Renders in --accent. */
  plus?: boolean;
}

export interface ScaleDef {
  id: ScaleId;
  label: string;
  unit: string;
  ticks: ScaleTick[];
  /** value → 0..1 dial position. */
  n: (v: number) => number;
  /** value → readout string. */
  fmt: (v: number) => string;
  /** Inverse of `n`: 0..1 dial position → value (for reading the needle back). */
  fromN: (n: number) => number;
}

// S-meter: each S-unit = 6 dB. S0 anchors the left edge, S9 sits at 0.625 of
// the dial (so the +20/+40/+60 dB region gets the right end). S9 = -73 dBm at
// HF; +60 over S9 = -13 dBm.
export const S_SCALE: ScaleDef = {
  id: 's',
  label: 'S',
  unit: '',
  ticks: [
    { v: 1, label: '1', major: true },
    { v: 3, label: '3', major: true },
    { v: 5, label: '5', major: true },
    { v: 7, label: '7', major: true },
    { v: 9, label: '9', major: true },
    { v: 11, label: '+20', major: true, plus: true },
    { v: 13, label: '+40', major: true, plus: true },
    { v: 15, label: '+60', major: true, plus: true },
  ],
  n: (s) => {
    if (s <= 9) return Math.max(0, s) / 9 * 0.625;
    const over = Math.min(s - 9, 6);
    return 0.625 + (over / 6) * 0.375;
  },
  fmt: (s) => {
    if (s <= 9) return `S${Math.round(Math.max(0, s))}`;
    const db = Math.round((s - 9) * 10);
    return `S9+${db}`;
  },
  fromN: (n) => {
    const c = Math.max(0, Math.min(1, n));
    if (c <= 0.625) return (c / 0.625) * 9;
    return 9 + ((c - 0.625) / 0.375) * 6;
  },
};

// Convert S-meter dial value (0..15) to dBm. S9 = -73 dBm, each S-unit 6 dB.
export function sToDbm(s: number): number {
  if (s <= 9) return -73 - (9 - s) * 6;
  return -73 + (s - 9) * 10;
}

// Convert RX dBm → S-meter dial value (the inverse used to drive the needle).
export function dbmToS(dbm: number): number {
  if (dbm <= -73) return Math.max(0, 9 + (dbm + 73) / 6);
  return 9 + (dbm + 73) / 10;
}

export const PO_SCALE: ScaleDef = {
  id: 'po',
  label: 'PO',
  unit: 'W',
  ticks: [
    { v: 0, label: '0', major: true },
    { v: 5, label: '5', major: false },
    { v: 10, label: '10', major: true },
    { v: 25, label: '25', major: true },
    { v: 50, label: '50', major: true },
    { v: 100, label: '100', major: true },
    { v: 150, label: '150', major: true },
  ],
  n: (w) => Math.min(1, Math.max(0, w) / 150),
  fmt: (w) => `${w < 10 ? w.toFixed(1) : Math.round(w)} W`,
  fromN: (n) => Math.max(0, Math.min(1, n)) * 150,
};

// SWR is logarithmic on the dial: 1.0 → 0, 10 → 1.0 (n = log10(swr)).
export const SWR_SCALE: ScaleDef = {
  id: 'swr',
  label: 'SWR',
  unit: '',
  ticks: [
    { v: 1.0, label: '1', major: true },
    { v: 1.5, label: '1.5', major: false },
    { v: 2.0, label: '2', major: true },
    { v: 3.0, label: '3', major: true },
    { v: 5.0, label: '5', major: false },
    { v: 10.0, label: '∞', major: true },
  ],
  n: (r) => {
    const v = Math.max(1, r);
    return Math.min(1, Math.log(v) / Math.log(10));
  },
  fmt: (r) => r.toFixed(1),
  fromN: (n) => Math.pow(10, Math.max(0, Math.min(1, n))),
};

export const SCALES: Record<ScaleId, ScaleDef> = {
  s: S_SCALE,
  po: PO_SCALE,
  swr: SWR_SCALE,
};

// Ballistics + averager primitives live in components/meters/ballistics.ts
// so the analog dial and the new shared meter hook (useBallisticReading) run
// the same math. Re-exported here so AnalogMeter's existing imports keep
// working unchanged.
export {
  ballisticsStep as ballistics,
  makeAverager,
  type Averager,
} from '../meters/ballistics';
