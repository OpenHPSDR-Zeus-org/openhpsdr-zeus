// SPDX-License-Identifier: GPL-2.0-or-later
//
// Display-informed NR supervisor. This does not process audio and does not
// replace WDSP; it inspects the current panadapter spectrum and picks a sane
// WDSP NR/blanker profile for the operator to apply explicitly.

import type { HardwareDspDiagnosticsDto, NrConfigDto, RxMode } from '../api/client';
import { clampFinite } from '../util/number';

export const SMART_NR_MIN_AGGRESSIVENESS = 0;
export const SMART_NR_MAX_AGGRESSIVENESS = 100;
export const SMART_NR_DEFAULT_AGGRESSIVENESS = 55;
export const SMART_NR_MIN_BLANKER_THRESHOLD = 8;
export const SMART_NR_MAX_BLANKER_THRESHOLD = 30;
export const SMART_NR_DEFAULT_MAX_BLANKER_THRESHOLD = 16;

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
  coherentPeakCount: number;
  coherentRidgeCount: number;
  widestCoherentRunBins: number;
  isolatedHotBinCount: number;
  confidenceAvailable: boolean;
  coherentSubthresholdSignal: boolean;
  rxAssistedWeakSignal: boolean;
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

export type SmartNrDspCapabilities = Pick<
  HardwareDspDiagnosticsDto,
  'wdspActive' | 'wdspEmnrPost2Available' | 'wdspNr4SbnrAvailable'
>;

export type SmartNrCapabilityAdaptation = {
  nr: NrConfigDto;
  capabilityLimited: boolean;
  capabilityRecommendation?: string;
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
  rx?: SmartNrRxContext | null;
  current: NrConfigDto;
  mode: RxMode;
};

export type SmartNrRxContext = {
  signalDbm?: number | null;
  adcHeadroomDb?: number | null;
  agcGain?: number | null;
};

export function labelSmartNrProfile(nr: NrConfigDto): string {
  if (nr.nbMode !== 'Off' && nr.snbEnabled) return `${nr.nbMode}+SNB`;
  if (nr.nrMode === 'Sbnr') return 'NR4';
  if (nr.nrMode === 'Emnr') return 'NR2';
  if (nr.nrMode === 'Anr') return 'NR1';
  if (nr.anfEnabled || nr.nbpNotchesEnabled) return 'Notch';
  return 'Light';
}

export function smartNrProfileKey(nr: NrConfigDto): string {
  return [
    nr.nrMode,
    nr.nbMode,
    nr.anfEnabled ? 'anf' : 'no-anf',
    nr.snbEnabled ? 'snb' : 'no-snb',
    nr.nbpNotchesEnabled ? 'nbp' : 'no-nbp',
  ].join('|');
}

export function shapeSmartNrRecommendation(rec: SmartNrRecommendation, tuning: SmartNrTuning): NrConfigDto {
  const next: NrConfigDto = { ...rec.nr };
  const aggressiveness = clampSmartNrAggressiveness(tuning.aggressiveness);
  const maxBlankerThreshold = clampSmartNrBlankerThreshold(tuning.maxBlankerThreshold);
  const gain = aggressiveness / 100;

  if (tuning.autoBlankerEnabled === false) {
    next.nbMode = 'Off';
    next.snbEnabled = false;
  } else if (next.nbMode !== 'Off') {
    next.nbThreshold = Math.min(clampSmartNrBlankerThreshold(next.nbThreshold), maxBlankerThreshold);
  }

  if (tuning.autoNotchEnabled === false) {
    next.anfEnabled = false;
    next.nbpNotchesEnabled = false;
  }

  if (next.nrMode === 'Sbnr') {
    const rxWeak = rec.condition.rxAssistedWeakSignal;
    next.nr4ReductionAmount = Math.round((rxWeak ? 4 : 5) + gain * (rxWeak ? 5 : 8));
    next.nr4SmoothingFactor = rec.condition.weakSparse
      ? Math.round((rxWeak ? 4 : 6) + gain * (rxWeak ? 4 : 5))
      : Math.round(8 + gain * 8);
    next.nr4WhiteningFactor = rec.condition.weakSparse
      ? Math.round((rxWeak ? 6 : 4) + gain * 6)
      : Math.round(gain * 5);
    next.nr4PostFilterThreshold = Math.round((rxWeak ? -12 : -10) + gain * (rxWeak ? 4 : 5));
  } else if (next.nrMode === 'Emnr') {
    const rxWeak = rec.condition.rxAssistedWeakSignal;
    const weak = rec.condition.weakSparse;
    next.emnrPost2Factor = Math.round((rxWeak ? 6 : weak ? 7 : 8) + gain * (rxWeak ? 8 : weak ? 8 : 10));
    next.emnrPost2Nlevel = Math.round((rxWeak ? 6 : weak ? 7 : 8) + gain * (rxWeak ? 6 : weak ? 8 : 10));
    next.emnrNpeMethod = rec.condition.denseNoise ? 1 : 0;
    next.emnrAeRun = gain >= 0.25;
  }

  if (aggressiveness < 25 && !rec.condition.impulsiveNoise && !rec.condition.tonalInterference) {
    next.nrMode = 'Off';
  }

  return next;
}

export function adaptSmartNrToDspCapabilities(
  nr: NrConfigDto,
  capabilities: SmartNrDspCapabilities | null,
): SmartNrCapabilityAdaptation {
  if (capabilities === null) {
    return { nr, capabilityLimited: false };
  }

  let next = nr;
  const notes: string[] = [];
  if (next.nrMode === 'Sbnr' && capabilities.wdspNr4SbnrAvailable === false) {
    next = {
      ...next,
      nrMode: 'Emnr',
      emnrGainMethod: 2,
      emnrNpeMethod: next.snbEnabled ? 1 : 0,
      emnrAeRun: true,
      emnrPost2Run: capabilities.wdspEmnrPost2Available !== false,
      emnrPost2Factor: next.nr4ReductionAmount ?? 12,
      emnrPost2Nlevel: next.nr4ReductionAmount ?? 12,
      emnrPost2Rate: 5,
      emnrPost2Taper: 12,
    };
    notes.push('NR4/SBNR unavailable in the active WDSP build; using NR2/EMNR fallback.');
  }

  if (next.nrMode === 'Emnr' && capabilities.wdspEmnrPost2Available === false && next.emnrPost2Run !== false) {
    next = {
      ...next,
      emnrPost2Run: false,
    };
    notes.push('NR2 post2 comfort-noise exports unavailable; running core EMNR without post2.');
  }

  return {
    nr: next,
    capabilityLimited: notes.length > 0,
    capabilityRecommendation: notes.length > 0 ? notes.join(' ') : undefined,
  };
}

function clampSmartNrAggressiveness(value: unknown): number {
  return clampFinite(
    value,
    SMART_NR_MIN_AGGRESSIVENESS,
    SMART_NR_MAX_AGGRESSIVENESS,
    SMART_NR_DEFAULT_AGGRESSIVENESS,
  );
}

function clampSmartNrBlankerThreshold(value: unknown): number {
  return clampFinite(
    value,
    SMART_NR_MIN_BLANKER_THRESHOLD,
    SMART_NR_MAX_BLANKER_THRESHOLD,
    SMART_NR_DEFAULT_MAX_BLANKER_THRESHOLD,
  );
}

function percentile(sorted: number[], q: number): number {
  if (sorted.length === 0) return 0;
  const idx = Math.max(0, Math.min(sorted.length - 1, Math.round((sorted.length - 1) * q)));
  return sorted[idx] ?? 0;
}

function fallbackFloorDb(spec: Float32Array): number {
  const sorted = Array.from(spec).filter(Number.isFinite).sort((a, b) => a - b);
  return percentile(sorted, 0.2) + 3;
}

function finiteSnr(sample: number, floorDb: number): number {
  return Number.isFinite(sample) && Number.isFinite(floorDb) ? sample - floorDb : 0;
}

function finiteConfidence(confidence: Float32Array | null, index: number, useConfidence: boolean): number {
  if (!useConfidence) return 1;
  const c = confidence![index]!;
  return Number.isFinite(c) ? Math.max(0, Math.min(1, c)) : 0;
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
    const v = finiteSnr(spectrum[i]!, f);
    const c = finiteConfidence(confidence, i, useConfidence);
    snr[i] = v;
    if (v > maxSnrDb) maxSnrDb = v;
    if (v >= 6) above6++;
    if (v >= 12) above12++;
    if (v >= 6 && c >= 0.45) coherentAbove6++;
    if (v >= 12 && c < 0.25) impulsiveAbove12++;
  }

  let peakCount = 0;
  let coherentPeakCount = 0;
  for (let i = 1; i < n - 1; i++) {
    const v = snr[i]!;
    const localPeak = v >= 10 && v >= snr[i - 1]! && v > snr[i + 1]!;
    if (localPeak) {
      peakCount++;
      if (!useConfidence || confidence![i]! >= 0.45) coherentPeakCount++;
    }
  }

  let coherentRidgeCount = 0;
  let widestCoherentRunBins = 0;
  let coherentRun = 0;
  const closeCoherentRun = () => {
    if (coherentRun === 0) return;
    coherentRidgeCount++;
    widestCoherentRunBins = Math.max(widestCoherentRunBins, coherentRun);
    coherentRun = 0;
  };

  let isolatedHotBinCount = 0;
  for (let i = 0; i < n; i++) {
    const v = snr[i]!;
    const c = finiteConfidence(confidence, i, useConfidence);
    const isCoherent = v >= 6 && c >= 0.45;
    if (isCoherent) {
      coherentRun++;
    } else {
      closeCoherentRun();
    }

    const left = i > 0 ? snr[i - 1]! : -Infinity;
    const right = i < n - 1 ? snr[i + 1]! : -Infinity;
    if (useConfidence && v >= 12 && c < 0.25 && left < 6 && right < 6) {
      isolatedHotBinCount++;
    }
  }
  closeCoherentRun();

  const sortedSnr = [...snr].sort((a, b) => a - b);
  const p50SnrDb = percentile(sortedSnr, 0.5);
  const p90SnrDb = percentile(sortedSnr, 0.9);
  const p98SnrDb = percentile(sortedSnr, 0.98);
  const occupancy6 = above6 / n;
  const occupancy12 = above12 / n;
  const coherentOccupancy6 = coherentAbove6 / n;
  const impulsiveOccupancy12 = impulsiveAbove12 / n;
  const isolatedImpulseFloor = Math.max(3, Math.ceil(n * 0.01));
  const impulsiveNoise =
    (impulsiveOccupancy12 > 0.018 || isolatedHotBinCount >= isolatedImpulseFloor) &&
    coherentOccupancy6 < occupancy6 * 0.5;
  const hasCoherentSignal = !useConfidence || coherentPeakCount > 0 || coherentRidgeCount > 0;
  // Temporal confidence can prove a persistent weak ridge before any single bin
  // reaches the normal 8 dB instantaneous signal gate.
  const coherentSubthresholdSignal =
    useConfidence &&
    maxSnrDb >= 6 &&
    maxSnrDb < 8 &&
    coherentRidgeCount > 0 &&
    widestCoherentRunBins <= Math.max(6, Math.ceil(n * 0.025)) &&
    coherentOccupancy6 > 0 &&
    coherentOccupancy6 < 0.05 &&
    occupancy12 === 0;
  const sparseCoherentRidge =
    !useConfidence || widestCoherentRunBins <= Math.max(8, Math.ceil(n * 0.04));
  const hasSignal = maxSnrDb >= 8 || coherentSubthresholdSignal;
  const weakSparse =
    !impulsiveNoise &&
    hasSignal &&
    hasCoherentSignal &&
    sparseCoherentRidge &&
    maxSnrDb < 24 &&
    occupancy12 < 0.08 &&
    coherentOccupancy6 < 0.12;
  const denseNoise =
    !impulsiveNoise &&
    (occupancy6 > 0.18 || p90SnrDb > 8 || occupancy12 > 0.12 || coherentOccupancy6 > 0.14);
  const tonalPeakCount = useConfidence ? coherentPeakCount : peakCount;
  const tonalInterference =
    !impulsiveNoise &&
    tonalPeakCount > 0 &&
    tonalPeakCount <= 24 &&
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
    coherentPeakCount,
    coherentRidgeCount,
    widestCoherentRunBins,
    isolatedHotBinCount,
    confidenceAvailable: useConfidence,
    coherentSubthresholdSignal,
    rxAssistedWeakSignal: false,
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

function finiteOrNull(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function applyRxWeakSignalEvidence(
  condition: SmartNrCondition,
  rx: SmartNrRxContext | null | undefined,
): SmartNrCondition {
  if (!rx || condition.denseNoise || condition.impulsiveNoise) return condition;

  const signalDbm = finiteOrNull(rx.signalDbm);
  const adcHeadroomDb = finiteOrNull(rx.adcHeadroomDb);
  const agcGain = finiteOrNull(rx.agcGain);
  const readableWeakSignal = signalDbm !== null && signalDbm > -145 && signalDbm < -112;
  const agcRecoveringWeakCopy = agcGain !== null && agcGain >= 30;
  const frontEndHasRoom = adcHeadroomDb === null || adcHeadroomDb >= 10;
  const faintSparseShape =
    condition.maxSnrDb >= 4 &&
    condition.occupancy12 < 0.04 &&
    condition.occupancy6 < 0.12 &&
    condition.widestCoherentRunBins <= Math.max(8, Math.ceil(condition.peakCount + 8));
  const confidenceAllows =
    !condition.confidenceAvailable ||
    condition.maxSnrDb < 6 ||
    condition.coherentPeakCount > 0 ||
    condition.coherentRidgeCount > 0 ||
    condition.coherentSubthresholdSignal;

  if (!readableWeakSignal || !agcRecoveringWeakCopy || !frontEndHasRoom || !faintSparseShape || !confidenceAllows) {
    return condition;
  }

  return {
    ...condition,
    rxAssistedWeakSignal: true,
    hasSignal: true,
    weakSparse: true,
  };
}

function isVoiceSsb(mode: RxMode): boolean {
  return mode === 'USB' || mode === 'LSB';
}

function isCarrierMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

function impulsiveBlankerThreshold(current: NrConfigDto): number {
  return Math.max(
    SMART_NR_MIN_BLANKER_THRESHOLD,
    Math.min(clampSmartNrBlankerThreshold(current.nbThreshold), SMART_NR_DEFAULT_MAX_BLANKER_THRESHOLD),
  );
}

function withNr4(current: NrConfigDto, c: SmartNrCondition, mode: RxMode): NrConfigDto {
  const weak = c.weakSparse;
  const rxWeak = c.rxAssistedWeakSignal;
  return {
    ...current,
    nrMode: 'Sbnr',
    anfEnabled: c.tonalInterference,
    snbEnabled: c.denseNoise || c.impulsiveNoise,
    nbpNotchesEnabled: c.tonalInterference,
    nbMode: c.impulsiveNoise ? 'Nb2' : 'Off',
    nbThreshold: c.impulsiveNoise ? impulsiveBlankerThreshold(current) : current.nbThreshold,
    nr4ReductionAmount: rxWeak ? 7 : weak ? 8 : 10,
    nr4SmoothingFactor: rxWeak ? (isCwOrDigital(mode) ? 5 : 10) : isCwOrDigital(mode) ? 8 : 14,
    nr4WhiteningFactor: rxWeak ? 10 : weak ? 8 : 4,
    nr4PostFilterThreshold: rxWeak ? -9 : weak ? -8 : -6,
    nr4NoiseScalingType: c.denseNoise ? 1 : 0,
  };
}

function withNr2(current: NrConfigDto, c: SmartNrCondition): NrConfigDto {
  const rxWeak = c.rxAssistedWeakSignal;
  return {
    ...current,
    nrMode: 'Emnr',
    anfEnabled: c.tonalInterference,
    snbEnabled: c.denseNoise || c.impulsiveNoise,
    nbpNotchesEnabled: c.tonalInterference,
    nbMode: c.impulsiveNoise ? 'Nb2' : 'Off',
    nbThreshold: c.impulsiveNoise ? impulsiveBlankerThreshold(current) : current.nbThreshold,
    emnrGainMethod: 2,
    emnrNpeMethod: c.denseNoise ? 1 : 0,
    emnrAeRun: true,
    emnrPost2Run: true,
    emnrPost2Factor: rxWeak ? 10 : c.weakSparse ? 12 : 15,
    emnrPost2Nlevel: rxWeak ? 10 : c.weakSparse ? 12 : 15,
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
    nbThreshold: c.impulsiveNoise ? impulsiveBlankerThreshold(current) : current.nbThreshold,
  };
}

export function recommendSmartNr(input: SmartNrInput): SmartNrRecommendation | null {
  const baseCondition = analyzeSmartNrCondition(input.spectrum, input.floor, input.confidence ?? null);
  if (!baseCondition) return null;
  const condition = applyRxWeakSignalEvidence(baseCondition, input.rx);

  let nr: NrConfigDto;
  let reason: string;
  if (isCwOrDigital(input.mode)) {
    nr = condition.hasSignal || condition.denseNoise
      ? withNr4(input.current, condition, input.mode)
      : quietProfile(input.current, condition);
    reason = condition.rxAssistedWeakSignal
      ? 'Weak-signal assist: AGC/RX telemetry confirms a marginal copy; use NR4/SBNR with narrow whitening.'
      : condition.weakSparse
      ? 'Weak narrow-signal profile: NR4/SBNR with mild whitening.'
      : condition.impulsiveNoise
        ? 'Impulsive-noise profile: engage NB2/SNB while keeping spectral NR conservative.'
      : 'CW/digital profile: spectral NR with conservative blanking.';
  } else if (isVoiceSsb(input.mode)) {
    nr = condition.weakSparse || condition.denseNoise
      ? withNr2(input.current, condition)
      : quietProfile(input.current, condition);
    reason = condition.rxAssistedWeakSignal
      ? 'Weak-signal assist: AGC/RX telemetry confirms marginal SSB copy; use low-artifact NR2/EMNR.'
      : condition.impulsiveNoise
      ? 'SSB impulse profile: use NB2/SNB for non-coherent spikes before heavier NR.'
      : condition.denseNoise
      ? 'SSB noise profile: NR2/EMNR with artifact eliminator and comfort noise.'
      : 'SSB clean/tonal profile: leave NR light and engage notch helpers only if needed.';
  } else if (isCarrierMode(input.mode)) {
    nr = condition.denseNoise || condition.weakSparse
      ? withNr4(input.current, condition, input.mode)
      : quietProfile(input.current, condition);
    reason = condition.rxAssistedWeakSignal
      ? 'Weak-signal assist: use mild NR4/SBNR while AGC preserves the carrier.'
      : condition.denseNoise
      ? 'Carrier-mode noise profile: mild NR4/SBNR without time-domain blanking.'
      : 'Carrier-mode clean profile: avoid unnecessary NR distortion.';
  } else {
    nr = quietProfile(input.current, condition);
    reason = 'Fallback profile: preserve audio and only handle tonal interference.';
  }

  return { nr, condition, reason };
}
