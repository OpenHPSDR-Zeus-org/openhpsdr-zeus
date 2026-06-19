// SPDX-License-Identifier: GPL-2.0-or-later

import type { RxMode } from '../../api/client';

export const DB_FLOOR = -130;
export const MINI_TRACE_RANGE_DB = 20;
export const MINI_NOISE_GATE_DB = 3.5;

const FIT_MIN_EDGE_HZ = 50;

function lowerSidebandMode(mode: RxMode): boolean {
  return mode === 'LSB' || mode === 'CWL';
}

function upperSidebandMode(mode: RxMode): boolean {
  return mode === 'USB' || mode === 'CWU';
}

// Map a clicked signal's VFO-relative energy extent to a passband that respects
// the active mode's sideband. Wrong-side SSB/CW clicks are intentionally ignored
// rather than flipping the passband to the opposite side of the carrier.
export function fitPassbandForMode(
  mode: RxMode,
  loOff: number,
  hiOff: number,
  margin: number,
): { low: number; high: number } | null {
  let low = Math.round(loOff - margin);
  let high = Math.round(hiOff + margin);
  if (lowerSidebandMode(mode)) {
    if (loOff >= 0) return null;
    high = Math.min(high, -FIT_MIN_EDGE_HZ);
  } else if (upperSidebandMode(mode)) {
    if (hiOff <= 0) return null;
    low = Math.max(low, FIT_MIN_EDGE_HZ);
  }
  if (high <= low + FIT_MIN_EDGE_HZ) return null;
  return { low, high };
}

export function frameBinRangeForHz(
  startHz: number,
  endHz: number,
  frameStartHz: number,
  binsPerHz: number,
  binsLength: number,
): [number, number] | null {
  if (binsLength <= 0 || binsPerHz <= 0 || endHz <= startHz) return null;
  const start = Math.max(0, Math.min(binsLength, Math.floor((startHz - frameStartHz) * binsPerHz)));
  const end = Math.max(0, Math.min(binsLength, Math.ceil((endHz - frameStartHz) * binsPerHz)));
  return end > start ? [start, end] : null;
}

export function sampleSpectrumAtHz(
  bins: Float32Array,
  absHz: number,
  frameStartHz: number,
  binsPerHz: number,
): number | null {
  if (bins.length === 0 || binsPerHz <= 0) return null;
  const fb = (absHz - frameStartHz) * binsPerHz;
  if (fb < 0 || fb > bins.length - 1) return null;
  const i0 = Math.max(0, Math.min(bins.length - 1, Math.floor(fb)));
  const i1 = Math.min(bins.length - 1, i0 + 1);
  const frac = fb - i0;
  return (bins[i0] ?? DB_FLOOR) * (1 - frac) + (bins[i1] ?? DB_FLOOR) * frac;
}

export function miniPanSignalLevel(
  db: number,
  floorDb: number,
  noiseGateDb = MINI_NOISE_GATE_DB,
  rangeDb = MINI_TRACE_RANGE_DB,
): number {
  const snr = db - floorDb;
  if (snr <= 0) return 0;
  const gate = Math.max(0.5, noiseGateDb);
  if (snr < gate) {
    return 0.055 * (snr / gate) ** 1.8;
  }
  const raw = (snr - Math.max(0, noiseGateDb)) / Math.max(1, rangeDb);
  return Math.min(1, raw) ** 0.70;
}

export function formatEqActualDb(db: number): string {
  if (!Number.isFinite(db)) return '--dB';
  const rounded = Math.max(0, Math.round(db));
  return `${rounded}dB`;
}
