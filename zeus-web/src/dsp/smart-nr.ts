// SPDX-License-Identifier: GPL-2.0-or-later
//
// Display-informed NR supervisor. This does not process audio and does not
// replace WDSP; it inspects the current panadapter spectrum and picks a sane
// WDSP NR/blanker profile for the operator to apply explicitly.

import type { NrConfigDto, RxMode } from '../api/client';

export type SmartNrCondition = {
  maxSnrDb: number;
  p50SnrDb: number;
  p90SnrDb: number;
  p98SnrDb: number;
  occupancy6: number;
  occupancy12: number;
  coherentOccupancy6: number;
  impulsiveOccupancy12: number;
  peakCount: number;
  hasSignal: boolean;
  weakSparse: boolean;
  denseNoise: boolean;
  impulsiveNoise: boolean;
  tonalInterference: boolean;
};

export type SmartNrRecommendation = {
  nr: NrConfigDto;
  condition: SmartNrCondition;
  reason: string;
};

export type SmartNrTuning = {
  aggressiveness: number;
  autoBlankerEnabled: boolean;
  autoNotchEnabled: boolean;
  maxBlankerThreshold: number;
};

export type SmartNrInput = {
  spectrum: Float32Array | null;
  floor: Float32Array | null;
  confidence?: Float32Array | null;
  current: NrConfigDto;
  mode: RxMode;
};

export function labelSmartNrProfile(nr: NrConfigDto): string {
  if (nr.nbMode !== 'Off' && nr.snbEnabled) return `${nr.nbMode}+SNB`;
  if (nr.nrMode === 'Sbnr') return 'NR4';
  if (nr.nrMode === 'Emnr') return 'NR2';
  if (nr.nrMode === 'Anr') return 'NR1';
  if (nr.anfEnabled || nr.nbpNotchesEnabled) return 'Notch';
  return 'Light';
}

export function shapeSmartNrRecommendation(rec: SmartNrRecommendation, tuning: SmartNrTuning): NrConfigDto {
  const next: NrConfigDto = { ...rec.nr };
  const gain = Math.max(0, Math.min(1, tuning.aggressiveness / 100));

  if (!tuning.autoBlankerEnabled) {
    next.nbMode = 'Off';
    next.snbEnabled = false;
  } else if (next.nbMode !== 'Off') {
    next.nbThreshold = Math.min(next.nbThreshold, tuning.maxBlankerThreshold);
  }

  if (!tuning.autoNotchEnabled) {
    next.anfEnabled = false;
    next.nbpNotchesEnabled = false;
  }

  if (next.nrMode === 'Sbnr') {
    next.nr4ReductionAmount = Math.round(5 + gain * 8);
    next.nr4WhiteningFactor = rec.condition.weakSparse ? Math.round(4 + gain * 6) : Math.round(gain * 5);
    next.nr4PostFilterThreshold = Math.round(-10 + gain * 5);
  } else if (next.nrMode === 'Emnr') {
    next.emnrPost2Factor = Math.round(8 + gain * 10);
    next.emnrPost2Nlevel = Math.round(8 + gain * 10);
    next.emnrAeRun = gain >= 0.25;
  }

  if (tuning.aggressiveness < 25 && !rec.condition.impulsiveNoise && !rec.condition.tonalInterference) {
    next.nrMode = 'Off';
  }

  return next;
}

function percentile(sorted: number[], q: number): number {
  if (sorted.length === 0) return 0;
  const idx = Math.max(0, Math.min(sorted.length - 1, Math.round((sorted.length - 1) * q)));
  return sorted[idx] ?? 0;
}

function fallbackFloorDb(spec: Float32Array): number {
  const sorted = Array.from(spec).sort((a, b) => a - b);
  return percentile(sorted, 0.2) + 3;
}

export function analyzeSmartNrCondition(
  spectrum: Float32Array | null,
  floor: Float32Array | null,
  confidence: Float32Array | null = null,
): SmartNrCondition | null {
  if (!spectrum || spectrum.length < 8) return null;
  const n = spectrum.length;
  const useFloor = floor !== null && floor.length === n;
  const useConfidence = confidence !== null && confidence.length === n;
  const globalFloor = useFloor ? 0 : fallbackFloorDb(spectrum);
  const snr = new Array<number>(n);
  let maxSnrDb = -Infinity;
  let above6 = 0;
  let above12 = 0;
  let coherentAbove6 = 0;
  let impulsiveAbove12 = 0;
  for (let i = 0; i < n; i++) {
    const f = useFloor ? floor![i]! : globalFloor;
    const v = spectrum[i]! - f;
    const c = useConfidence ? confidence![i]! : 1;
    snr[i] = v;
    if (v > maxSnrDb) maxSnrDb = v;
    if (v >= 6) above6++;
    if (v >= 12) above12++;
    if (v >= 6 && c >= 0.45) coherentAbove6++;
    if (v >= 12 && c < 0.25) impulsiveAbove12++;
  }

  let peakCount = 0;
  for (let i = 1; i < n - 1; i++) {
    const v = snr[i]!;
    if (v >= 10 && v >= snr[i - 1]! && v > snr[i + 1]!) peakCount++;
  }

  const sortedSnr = [...snr].sort((a, b) => a - b);
  const p50SnrDb = percentile(sortedSnr, 0.5);
  const p90SnrDb = percentile(sortedSnr, 0.9);
  const p98SnrDb = percentile(sortedSnr, 0.98);
  const occupancy6 = above6 / n;
  const occupancy12 = above12 / n;
  const coherentOccupancy6 = coherentAbove6 / n;
  const impulsiveOccupancy12 = impulsiveAbove12 / n;
  const hasSignal = maxSnrDb >= 8;
  const impulsiveNoise = impulsiveOccupancy12 > 0.018 && coherentOccupancy6 < occupancy6 * 0.5;
  const weakSparse =
    !impulsiveNoise &&
    hasSignal &&
    maxSnrDb < 24 &&
    occupancy12 < 0.08 &&
    coherentOccupancy6 < 0.12;
  const denseNoise =
    !impulsiveNoise &&
    (occupancy6 > 0.18 || p90SnrDb > 8 || occupancy12 > 0.12 || coherentOccupancy6 > 0.14);
  const tonalInterference =
    peakCount > 0 &&
    peakCount <= 24 &&
    maxSnrDb >= 18 &&
    occupancy12 < 0.12;

  return {
    maxSnrDb,
    p50SnrDb,
    p90SnrDb,
    p98SnrDb,
    occupancy6,
    occupancy12,
    coherentOccupancy6,
    impulsiveOccupancy12,
    peakCount,
    hasSignal,
    weakSparse,
    denseNoise,
    impulsiveNoise,
    tonalInterference,
  };
}

function isCwOrDigital(mode: RxMode): boolean {
  return mode === 'CWU' || mode === 'CWL' || mode === 'DIGU' || mode === 'DIGL';
}

function isVoiceSsb(mode: RxMode): boolean {
  return mode === 'USB' || mode === 'LSB';
}

function isCarrierMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

function withNr4(current: NrConfigDto, c: SmartNrCondition, mode: RxMode): NrConfigDto {
  const weak = c.weakSparse;
  return {
    ...current,
    nrMode: 'Sbnr',
    anfEnabled: c.tonalInterference,
    snbEnabled: c.denseNoise || c.impulsiveNoise,
    nbpNotchesEnabled: c.tonalInterference,
    nbMode: c.impulsiveNoise ? 'Nb2' : 'Off',
    nbThreshold: c.impulsiveNoise ? Math.max(8, Math.min(current.nbThreshold, 16)) : current.nbThreshold,
    nr4ReductionAmount: weak ? 8 : 10,
    nr4SmoothingFactor: isCwOrDigital(mode) ? 8 : 14,
    nr4WhiteningFactor: weak ? 8 : 4,
    nr4NoiseRescale: 2,
    nr4PostFilterThreshold: weak ? -8 : -6,
    nr4NoiseScalingType: c.denseNoise ? 1 : 0,
  };
}

function withNr2(current: NrConfigDto, c: SmartNrCondition): NrConfigDto {
  return {
    ...current,
    nrMode: 'Emnr',
    anfEnabled: c.tonalInterference,
    snbEnabled: c.denseNoise || c.impulsiveNoise,
    nbpNotchesEnabled: c.tonalInterference,
    nbMode: c.impulsiveNoise ? 'Nb2' : 'Off',
    nbThreshold: c.impulsiveNoise ? Math.max(8, Math.min(current.nbThreshold, 16)) : current.nbThreshold,
    emnrGainMethod: 2,
    emnrNpeMethod: c.denseNoise ? 1 : 0,
    emnrAeRun: true,
    emnrPost2Run: true,
    emnrPost2Factor: c.weakSparse ? 12 : 15,
    emnrPost2Nlevel: c.weakSparse ? 12 : 15,
    emnrPost2Rate: 5,
    emnrPost2Taper: 12,
  };
}

function quietProfile(current: NrConfigDto, c: SmartNrCondition): NrConfigDto {
  return {
    ...current,
    nrMode: 'Off',
    anfEnabled: c.tonalInterference,
    snbEnabled: c.impulsiveNoise,
    nbpNotchesEnabled: c.tonalInterference,
    nbMode: c.impulsiveNoise ? 'Nb2' : 'Off',
    nbThreshold: c.impulsiveNoise ? Math.max(8, Math.min(current.nbThreshold, 16)) : current.nbThreshold,
  };
}

export function recommendSmartNr(input: SmartNrInput): SmartNrRecommendation | null {
  const condition = analyzeSmartNrCondition(input.spectrum, input.floor, input.confidence ?? null);
  if (!condition) return null;

  let nr: NrConfigDto;
  let reason: string;
  if (isCwOrDigital(input.mode)) {
    nr = condition.hasSignal || condition.denseNoise
      ? withNr4(input.current, condition, input.mode)
      : quietProfile(input.current, condition);
    reason = condition.weakSparse
      ? 'Weak narrow-signal profile: NR4/SBNR with mild whitening.'
      : condition.impulsiveNoise
        ? 'Impulsive-noise profile: engage NB2/SNB while keeping spectral NR conservative.'
      : 'CW/digital profile: spectral NR with conservative blanking.';
  } else if (isVoiceSsb(input.mode)) {
    nr = condition.weakSparse || condition.denseNoise
      ? withNr2(input.current, condition)
      : quietProfile(input.current, condition);
    reason = condition.impulsiveNoise
      ? 'SSB impulse profile: use NB2/SNB for non-coherent spikes before heavier NR.'
      : condition.denseNoise
      ? 'SSB noise profile: NR2/EMNR with artifact eliminator and comfort noise.'
      : 'SSB clean/tonal profile: leave NR light and engage notch helpers only if needed.';
  } else if (isCarrierMode(input.mode)) {
    nr = condition.denseNoise || condition.weakSparse
      ? withNr4(input.current, condition, input.mode)
      : quietProfile(input.current, condition);
    reason = condition.denseNoise
      ? 'Carrier-mode noise profile: mild NR4/SBNR without time-domain blanking.'
      : 'Carrier-mode clean profile: avoid unnecessary NR distortion.';
  } else {
    nr = quietProfile(input.current, condition);
    reason = 'Fallback profile: preserve audio and only handle tonal interference.';
  }

  return { nr, condition, reason };
}
