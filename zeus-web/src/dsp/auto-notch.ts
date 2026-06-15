// SPDX-License-Identifier: GPL-2.0-or-later
//
// Detect persistent narrow EMF/RFI bars and express them as manual-notch bands.

import type { Notch } from '../state/notch-store';

export type AutoNotchCandidate = {
  centerHz: number;
  widthHz: number;
  snrDb: number;
  confidence: number;
};

export type AutoNotchTrack = AutoNotchCandidate & {
  hits: number;
  misses: number;
  verified: boolean;
};

export type AutoNotchInput = {
  spectrum: Float32Array | null;
  floor: Float32Array | null;
  confidence: Float32Array | null;
  centerHz: bigint | number;
  hzPerPixel: number;
  existingNotches?: readonly Notch[];
};

export type AutoNotchTrackerOptions = {
  verifySamples?: number;
  provisionalMisses?: number;
  holdMisses?: number;
  matchHz?: number;
  refineAlpha?: number;
};

export type AutoNotchTracker = {
  update: (candidates: readonly AutoNotchCandidate[], manualNotches?: readonly Notch[]) => AutoNotchTrack[];
  clear: () => void;
  snapshot: () => AutoNotchTrack[];
};

const MIN_SNR_DB = 14;
const MIN_CONFIDENCE = 0.52;
const MIN_WIDTH_HZ = 45;
const MAX_WIDTH_HZ = 750;
const EDGE_PAD_HZ = 40;
const MERGE_HZ = 120;
const MAX_AUTO_NOTCHES = 16;
const VERIFY_SAMPLES = 3;
const PROVISIONAL_MISSES = 2;
const VERIFIED_HOLD_MISSES = 8;
const TRACK_MATCH_HZ = 180;
const REFINE_ALPHA = 0.22;
const OUTPUT_QUANTUM_HZ = 10;

function finite(v: number): boolean {
  return Number.isFinite(v);
}

function binToHz(bin: number, n: number, centerHz: number, hzPerPixel: number): number {
  return centerHz + (bin - n / 2) * hzPerPixel;
}

function overlaps(aCenter: number, aWidth: number, bCenter: number, bWidth: number): boolean {
  return Math.abs(aCenter - bCenter) <= (aWidth + bWidth) / 2;
}

function isCoveredByManualNotch(
  candidate: AutoNotchCandidate,
  notches: readonly Notch[],
): boolean {
  return notches.some((n) =>
    n.source !== 'auto' && overlaps(candidate.centerHz, candidate.widthHz, n.centerHz, n.widthHz),
  );
}

function candidateSort(a: AutoNotchCandidate, b: AutoNotchCandidate): number {
  const as = a.snrDb * (0.6 + a.confidence);
  const bs = b.snrDb * (0.6 + b.confidence);
  return bs - as;
}

function clampWidth(widthHz: number): number {
  return Math.max(MIN_WIDTH_HZ, Math.min(MAX_WIDTH_HZ, widthHz));
}

function quantizeHz(value: number): number {
  return Math.round(value / OUTPUT_QUANTUM_HZ) * OUTPUT_QUANTUM_HZ;
}

function cloneTrack(t: AutoNotchTrack): AutoNotchTrack {
  return { ...t };
}

function outputTrack(t: AutoNotchTrack): AutoNotchTrack {
  return {
    ...t,
    centerHz: quantizeHz(t.centerHz),
    widthHz: clampWidth(quantizeHz(t.widthHz)),
  };
}

function isCoveredByManualTrack(track: AutoNotchTrack, notches: readonly Notch[]): boolean {
  return isCoveredByManualNotch(track, notches);
}

export function createAutoNotchTracker(options: AutoNotchTrackerOptions = {}): AutoNotchTracker {
  const verifySamples = Math.max(1, Math.round(options.verifySamples ?? VERIFY_SAMPLES));
  const provisionalMisses = Math.max(0, Math.round(options.provisionalMisses ?? PROVISIONAL_MISSES));
  const holdMisses = Math.max(provisionalMisses, Math.round(options.holdMisses ?? VERIFIED_HOLD_MISSES));
  const matchHz = Math.max(20, options.matchHz ?? TRACK_MATCH_HZ);
  const refineAlpha = Math.max(0.02, Math.min(1, options.refineAlpha ?? REFINE_ALPHA));
  let tracks: AutoNotchTrack[] = [];

  const clear = () => {
    tracks = [];
  };

  const snapshot = () => tracks.map(cloneTrack);

  const update = (
    candidates: readonly AutoNotchCandidate[],
    manualNotches: readonly Notch[] = [],
  ): AutoNotchTrack[] => {
    const ordered = [...candidates].sort(candidateSort);
    const used = new Set<number>();

    for (const track of tracks) {
      let bestIdx = -1;
      let bestDistance = Infinity;
      for (let i = 0; i < ordered.length; i++) {
        if (used.has(i)) continue;
        const candidate = ordered[i]!;
        const distance = Math.abs(candidate.centerHz - track.centerHz);
        const gate = Math.max(matchHz, (candidate.widthHz + track.widthHz) / 2);
        if (distance <= gate && distance < bestDistance) {
          bestDistance = distance;
          bestIdx = i;
        }
      }

      if (bestIdx >= 0) {
        const candidate = ordered[bestIdx]!;
        used.add(bestIdx);
        track.centerHz += (candidate.centerHz - track.centerHz) * refineAlpha;
        track.widthHz = clampWidth(track.widthHz + (candidate.widthHz - track.widthHz) * refineAlpha);
        track.snrDb += (candidate.snrDb - track.snrDb) * refineAlpha;
        track.confidence += (candidate.confidence - track.confidence) * refineAlpha;
        track.hits += 1;
        track.misses = 0;
        if (track.hits >= verifySamples) track.verified = true;
      } else {
        track.misses += 1;
      }
    }

    for (let i = 0; i < ordered.length; i++) {
      if (used.has(i)) continue;
      const candidate = ordered[i]!;
      tracks.push({
        centerHz: candidate.centerHz,
        widthHz: clampWidth(candidate.widthHz),
        snrDb: candidate.snrDb,
        confidence: candidate.confidence,
        hits: 1,
        misses: 0,
        verified: verifySamples <= 1,
      });
    }

    tracks = tracks
      .filter((track) => !isCoveredByManualTrack(track, manualNotches))
      .filter((track) => track.misses <= (track.verified ? holdMisses : provisionalMisses))
      .sort(candidateSort)
      .slice(0, MAX_AUTO_NOTCHES)
      .sort((a, b) => a.centerHz - b.centerHz);

    return tracks
      .filter((track) => track.verified)
      .map(outputTrack);
  };

  return { update, clear, snapshot };
}

export function detectAutoNotches(input: AutoNotchInput): AutoNotchCandidate[] {
  const spec = input.spectrum;
  const floor = input.floor;
  const conf = input.confidence;
  const hzPerPixel = input.hzPerPixel;
  if (!spec || !floor || !conf || spec.length < 8) return [];
  const n = spec.length;
  if (floor.length !== n || conf.length !== n || !finite(hzPerPixel) || hzPerPixel <= 0) return [];
  const centerHz = Number(input.centerHz);
  if (!finite(centerHz)) return [];

  const raw: AutoNotchCandidate[] = [];
  let i = 1;
  while (i < n - 1) {
    const snr = spec[i]! - floor[i]!;
    const c = conf[i]!;
    if (!finite(snr) || !finite(c) || snr < MIN_SNR_DB || c < MIN_CONFIDENCE) {
      i++;
      continue;
    }

    let lo = i;
    let hi = i;
    let crest = i;
    let crestSnr = snr;
    let confidenceSum = 0;
    let bins = 0;

    while (lo > 0) {
      const leftSnr = spec[lo - 1]! - floor[lo - 1]!;
      const leftConf = conf[lo - 1]!;
      if (!finite(leftSnr) || !finite(leftConf) || leftSnr < MIN_SNR_DB - 2 || leftConf < MIN_CONFIDENCE * 0.8) break;
      lo--;
    }
    while (hi < n - 1) {
      const rightSnr = spec[hi + 1]! - floor[hi + 1]!;
      const rightConf = conf[hi + 1]!;
      if (!finite(rightSnr) || !finite(rightConf) || rightSnr < MIN_SNR_DB - 2 || rightConf < MIN_CONFIDENCE * 0.8) break;
      hi++;
    }

    for (let k = lo; k <= hi; k++) {
      const kSnr = spec[k]! - floor[k]!;
      if (kSnr > crestSnr) {
        crest = k;
        crestSnr = kSnr;
      }
      confidenceSum += Math.max(0, Math.min(1, conf[k]!));
      bins++;
    }

    const occupiedHz = Math.max(hzPerPixel, (hi - lo + 1) * hzPerPixel);
    if (occupiedHz <= MAX_WIDTH_HZ) {
      const widthHz = Math.max(
        MIN_WIDTH_HZ,
        Math.min(MAX_WIDTH_HZ, occupiedHz + EDGE_PAD_HZ + hzPerPixel * 2),
      );
      const candidate = {
        centerHz: binToHz(crest, n, centerHz, hzPerPixel),
        widthHz,
        snrDb: crestSnr,
        confidence: bins > 0 ? confidenceSum / bins : c,
      };
      if (!isCoveredByManualNotch(candidate, input.existingNotches ?? [])) {
        raw.push(candidate);
      }
    }

    i = hi + 1;
  }

  raw.sort(candidateSort);
  const accepted: AutoNotchCandidate[] = [];
  for (const c of raw) {
    if (accepted.length >= MAX_AUTO_NOTCHES) break;
    if (accepted.some((a) => Math.abs(a.centerHz - c.centerHz) <= MERGE_HZ)) continue;
    accepted.push(c);
  }
  return accepted.sort((a, b) => a.centerHz - b.centerHz);
}
