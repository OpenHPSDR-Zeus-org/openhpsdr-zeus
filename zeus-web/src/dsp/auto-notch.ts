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
  locked: boolean;
  lockedCenterHz: number;
  lockedWidthHz: number;
  centerJitterHz: number;
  widthJitterHz: number;
};

export type AutoNotchInput = {
  spectrum: Float32Array | null;
  floor: Float32Array | null;
  /** Per-bin temporal coherence (legacy/diagnostic; not a detection gate). */
  confidence: Float32Array | null;
  /** Per-bin amplitude steadiness 0..1 — the carrier-vs-voice discriminant. */
  stationarity: Float32Array | null;
  centerHz: bigint | number;
  hzPerPixel: number;
  existingNotches?: readonly Notch[];
  /** Tuned receiver passband. Candidates overlapping it are treated as wanted signal. */
  tunedPassband?: AutoNotchPassband | null;
  /** Absolute-Hz windows the detector must never notch (e.g. FT8/FT4 segments). */
  protectedRanges?: readonly { lowHz: number; highHz: number }[];
};

export type AutoNotchPassband = {
  /** Absolute dial/VFO Hz for the receiver the operator is listening to. */
  dialHz: number;
  /** Audio filter offsets from dial/VFO, signed for the current demod sideband. */
  lowOffsetHz: number;
  highOffsetHz: number;
  /** Extra Hz on each side protected from auto notches. Defaults to a tight guard. */
  guardHz?: number;
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

// Carrier-line detection gates. The detector's only job is to find STEADY,
// NARROW carriers/heterodynes the operator wants notched and to leave SSB/AM
// voice alone. Three orthogonal gates, ANDed together (see
// docs/designs/auto-notch-carrier-detector.md):
//
//   • PROMINENCE — how far the peak stands above its own local saddle (the
//     higher of the two basin minima bounding it), measured topographically.
//     This REPLACES "SNR above the CFAR floor" as the primary gate: a strong
//     carrier whose own FFT leakage skirts lift the spatial floor (CFAR self-
//     masking) still towers over its local saddles, so it is no longer missed.
//     A voice formant riding a broad hump has little prominence over the hump.
//   • NARROWNESS — width measured at a fixed dB drop from the peak. A carrier is
//     1–3 bins wide; an SSB voice hump stays up for hundreds of Hz to ~2.7 kHz.
//     This is what stops the low-end of voice from ever qualifying.
//   • STATIONARITY — amplitude steadiness over time (signal-estimator). A
//     carrier's level is constant; voice swings with the 2–10 Hz syllabic
//     envelope. This is the decisive carrier-vs-voice discriminant.
const MIN_SNR_DB = 6; // light prefilter — prominence is the real level gate
const MIN_PROMINENCE_DB = 8; // peak must stand this far above its local saddle
const PROMINENCE_WINDOW_HZ = 3_000; // ± window the saddle reference is taken over
const WIDTH_DROP_DB = 6; // carrier width is measured at −6 dB from the peak
const MAX_CARRIER_WIDTH_HZ = 500; // wider than this is a signal/voice, not a carrier
const OCCUPIED_SHOULDER_SNR_DB = 6; // broad above-floor energy around a peak is voice/signal body
const OCCUPIED_SHOULDER_MAX_HZ = 1_250;
const OCCUPIED_SHOULDER_MAX_MULTIPLIER = 2.5;
// At a wide display span one FFT bin can be hundreds of Hz, so a genuine narrow
// carrier smears across 2–3 bins and would blow the fixed Hz limit above — which
// is why strong carriers were missed on a full-band view. Allow the narrowness
// limit to grow to a few bins so the gate stays "a few bins wide", not "≤ 500 Hz
// regardless of zoom". Voice still spans far more bins than this, so it is still
// rejected at every zoom.
const CARRIER_MAX_BINS = 4;
const MIN_STEADINESS = 0.45; // amplitude-stationarity floor (0..1). Real EMF/RFI
// is narrow + persistent but not always dead-steady (line-harmonic buzz, QSB),
// so this is a low floor that only sheds wildly fluctuating bins — narrowness +
// prominence are the real voice rejectors. Carriers typically read ≳ 0.7.
const NOTCH_PAD_HZ = 30; // pad each side of the measured carrier width
const MIN_WIDTH_HZ = 45;
const NARROW_MAX_WIDTH_HZ = 750; // tracker required-hits boundary
const MAX_WIDTH_HZ = 600; // an emitted notch never exceeds a narrow carrier band
const MERGE_HZ = 120;
const MAX_AUTO_NOTCHES = 16;
const VERIFY_SAMPLES = 3;
const WIDE_VERIFY_SAMPLES = 5;
const PROVISIONAL_MISSES = 2;
const VERIFIED_HOLD_MISSES = 8;
const TRACK_MATCH_HZ = 180;
const REFINE_ALPHA = 0.22;
const OUTPUT_QUANTUM_HZ = 10;
const TRACK_JITTER_ALPHA = 0.28;
const LOCKED_REFINE_ALPHA = 0.12;
const CENTER_JITTER_MIN_HZ = 90;
const CENTER_JITTER_MAX_HZ = 900;
const CENTER_JITTER_WIDTH_FRACTION = 0.22;
const WIDTH_JITTER_MIN_HZ = 120;
const WIDTH_JITTER_MAX_HZ = 1_500;
const WIDTH_JITTER_WIDTH_FRACTION = 0.35;
const LOCK_DRIFT_WIDTH_FRACTION = 0.45;
const PASSBAND_GUARD_HZ = 50;

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

/** Topographic prominence of the peak at `i`, in dB: peak minus the higher of
 *  the two local saddle minima within ±windowBins. Walking outward stops when
 *  the spectrum rises above the peak (the basin edge) or the window/array ends.
 *  Robust to a tilted or skirt-lifted baseline in a way that peak-minus-CFAR is
 *  not — this is the fix for a strong carrier self-masking its own floor. */
function saddleProminenceDb(spec: Float32Array, i: number, windowBins: number): number {
  const peak = spec[i]!;
  const loEdge = Math.max(0, i - windowBins);
  const hiEdge = Math.min(spec.length - 1, i + windowBins);
  let leftMin = peak;
  for (let j = i - 1; j >= loEdge; j--) {
    const v = spec[j]!;
    if (!finite(v) || v > peak) break;
    if (v < leftMin) leftMin = v;
  }
  let rightMin = peak;
  for (let j = i + 1; j <= hiEdge; j++) {
    const v = spec[j]!;
    if (!finite(v) || v > peak) break;
    if (v < rightMin) rightMin = v;
  }
  return peak - Math.max(leftMin, rightMin);
}

/** Width in Hz of the contiguous run around the peak that stays at or above
 *  `threshold` dB (peak − WIDTH_DROP_DB). A carrier collapses within a couple of
 *  bins; a voice hump stays above the −6 dB line for hundreds of Hz. */
function widthAtDropHz(spec: Float32Array, i: number, threshold: number, hzPerPixel: number): number {
  let lo = i;
  let hi = i;
  while (lo > 0 && finite(spec[lo - 1]!) && spec[lo - 1]! >= threshold) lo--;
  while (hi < spec.length - 1 && finite(spec[hi + 1]!) && spec[hi + 1]! >= threshold) hi++;
  return (hi - lo + 1) * hzPerPixel;
}

function occupiedShoulderWidthHz(
  spec: Float32Array,
  floor: Float32Array,
  i: number,
  hzPerPixel: number,
): number {
  let lo = i;
  let hi = i;
  while (
    lo > 0 &&
    finite(spec[lo - 1]!) &&
    finite(floor[lo - 1]!) &&
    spec[lo - 1]! - floor[lo - 1]! >= OCCUPIED_SHOULDER_SNR_DB
  ) {
    lo--;
  }
  while (
    hi < spec.length - 1 &&
    finite(spec[hi + 1]!) &&
    finite(floor[hi + 1]!) &&
    spec[hi + 1]! - floor[hi + 1]! >= OCCUPIED_SHOULDER_SNR_DB
  ) {
    hi++;
  }
  return (hi - lo + 1) * hzPerPixel;
}

function occupiedShoulderLimitHz(maxCarrierWidthHz: number): number {
  return Math.max(OCCUPIED_SHOULDER_MAX_HZ, maxCarrierWidthHz * OCCUPIED_SHOULDER_MAX_MULTIPLIER);
}

function quantizeHz(value: number): number {
  return Math.round(value / OUTPUT_QUANTUM_HZ) * OUTPUT_QUANTUM_HZ;
}

function cloneTrack(t: AutoNotchTrack): AutoNotchTrack {
  return { ...t };
}

function outputTrack(t: AutoNotchTrack): AutoNotchTrack {
  const centerHz = t.locked ? t.lockedCenterHz : t.centerHz;
  const widthHz = t.locked ? t.lockedWidthHz : t.widthHz;
  return {
    ...t,
    centerHz: quantizeHz(centerHz),
    widthHz: clampWidth(quantizeHz(widthHz)),
  };
}

function isCoveredByManualTrack(track: AutoNotchTrack, notches: readonly Notch[]): boolean {
  return isCoveredByManualNotch(track, notches);
}

function protectedPassbandRange(
  passband: AutoNotchPassband | null | undefined,
): { lowHz: number; highHz: number; guardHz: number } | null {
  if (!passband) return null;
  const dialHz = passband.dialHz;
  const lowOffsetHz = passband.lowOffsetHz;
  const highOffsetHz = passband.highOffsetHz;
  if (!finite(dialHz) || !finite(lowOffsetHz) || !finite(highOffsetHz)) return null;
  const lowHz = dialHz + Math.min(lowOffsetHz, highOffsetHz);
  const highHz = dialHz + Math.max(lowOffsetHz, highOffsetHz);
  const guardHz = finite(passband.guardHz ?? Number.NaN)
    ? Math.max(0, passband.guardHz!)
    : PASSBAND_GUARD_HZ;
  return { lowHz, highHz, guardHz };
}

function overlapsProtectedPassband(
  centerHz: number,
  widthHz: number,
  passband: AutoNotchPassband | null | undefined,
): boolean {
  const range = protectedPassbandRange(passband);
  if (!range) return false;
  const lo = centerHz - widthHz / 2;
  const hi = centerHz + widthHz / 2;
  return hi >= range.lowHz - range.guardHz && lo <= range.highHz + range.guardHz;
}

export function createAutoNotchTracker(options: AutoNotchTrackerOptions = {}): AutoNotchTracker {
  const verifySamples = Math.max(1, Math.round(options.verifySamples ?? VERIFY_SAMPLES));
  const provisionalMisses = Math.max(0, Math.round(options.provisionalMisses ?? PROVISIONAL_MISSES));
  const holdMisses = Math.max(provisionalMisses, Math.round(options.holdMisses ?? VERIFIED_HOLD_MISSES));
  const matchHz = Math.max(20, options.matchHz ?? TRACK_MATCH_HZ);
  const refineAlpha = Math.max(0.02, Math.min(1, options.refineAlpha ?? REFINE_ALPHA));
  let tracks: AutoNotchTrack[] = [];

  const requiredHits = (track: AutoNotchTrack): number =>
    track.widthHz > NARROW_MAX_WIDTH_HZ ? Math.max(verifySamples, WIDE_VERIFY_SAMPLES) : verifySamples;

  const stableEnough = (track: AutoNotchTrack): boolean => {
    const centerLimit = Math.max(
      CENTER_JITTER_MIN_HZ,
      Math.min(CENTER_JITTER_MAX_HZ, track.widthHz * CENTER_JITTER_WIDTH_FRACTION),
    );
    const widthLimit = Math.max(
      WIDTH_JITTER_MIN_HZ,
      Math.min(WIDTH_JITTER_MAX_HZ, track.widthHz * WIDTH_JITTER_WIDTH_FRACTION),
    );
    return track.centerJitterHz <= centerLimit && track.widthJitterHz <= widthLimit;
  };

  const lockIfReady = (track: AutoNotchTrack): void => {
    if (track.verified || track.hits < requiredHits(track) || !stableEnough(track)) return;
    const out = outputTrack(track);
    track.verified = true;
    track.locked = true;
    track.lockedCenterHz = out.centerHz;
    track.lockedWidthHz = out.widthHz;
  };

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
        if (
          track.locked &&
          Math.abs(candidate.centerHz - track.lockedCenterHz) >
            Math.max(matchHz, track.lockedWidthHz * LOCK_DRIFT_WIDTH_FRACTION)
        ) {
          continue;
        }
        if (distance <= gate && distance < bestDistance) {
          bestDistance = distance;
          bestIdx = i;
        }
      }

      if (bestIdx >= 0) {
        const candidate = ordered[bestIdx]!;
        used.add(bestIdx);
        const centerDelta = Math.abs(candidate.centerHz - track.centerHz);
        const widthDelta = Math.abs(candidate.widthHz - track.widthHz);
        track.centerJitterHz += (centerDelta - track.centerJitterHz) * TRACK_JITTER_ALPHA;
        track.widthJitterHz += (widthDelta - track.widthJitterHz) * TRACK_JITTER_ALPHA;
        track.centerHz += (candidate.centerHz - track.centerHz) * refineAlpha;
        track.widthHz = clampWidth(track.widthHz + (candidate.widthHz - track.widthHz) * refineAlpha);
        track.snrDb += (candidate.snrDb - track.snrDb) * refineAlpha;
        track.confidence += (candidate.confidence - track.confidence) * refineAlpha;
        track.hits += 1;
        track.misses = 0;
        if (track.locked) {
          track.lockedCenterHz += (track.centerHz - track.lockedCenterHz) * LOCKED_REFINE_ALPHA;
          track.lockedWidthHz = clampWidth(track.lockedWidthHz + (track.widthHz - track.lockedWidthHz) * LOCKED_REFINE_ALPHA);
        }
        lockIfReady(track);
      } else {
        track.misses += 1;
      }
    }

    for (let i = 0; i < ordered.length; i++) {
      if (used.has(i)) continue;
      const candidate = ordered[i]!;
      const track: AutoNotchTrack = {
        centerHz: candidate.centerHz,
        widthHz: clampWidth(candidate.widthHz),
        snrDb: candidate.snrDb,
        confidence: candidate.confidence,
        hits: 1,
        misses: 0,
        verified: false,
        locked: false,
        lockedCenterHz: candidate.centerHz,
        lockedWidthHz: clampWidth(candidate.widthHz),
        centerJitterHz: 0,
        widthJitterHz: 0,
      };
      lockIfReady(track);
      tracks.push(track);
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
  const steady = input.stationarity;
  const hzPerPixel = input.hzPerPixel;
  // Stationarity is required: without it we cannot tell a carrier from voice,
  // and emitting on prominence alone would re-introduce the voice-eating bug.
  // Fail safe to "no notches" rather than guess.
  if (!spec || !floor || !steady || spec.length < 8) return [];
  const n = spec.length;
  if (floor.length !== n || steady.length !== n || !finite(hzPerPixel) || hzPerPixel <= 0) return [];
  const centerHz = Number(input.centerHz);
  if (!finite(centerHz)) return [];

  const windowBins = Math.max(2, Math.round(PROMINENCE_WINDOW_HZ / hzPerPixel));
  const maxCarrierWidthHz = Math.max(MAX_CARRIER_WIDTH_HZ, CARRIER_MAX_BINS * hzPerPixel);
  const protectedRanges = input.protectedRanges ?? [];
  const raw: AutoNotchCandidate[] = [];

  for (let i = 1; i < n - 1; i++) {
    const here = spec[i]!;
    const left = spec[i - 1]!;
    const right = spec[i + 1]!;
    if (!finite(here) || !finite(left) || !finite(right)) continue;
    // Local maximum. `>=` on the left lets a flat-topped two-bin carrier anchor
    // on its left bin (the right `>` keeps it from anchoring twice).
    if (!(here >= left && here > right)) continue;

    // Gate 1 — light level prefilter (cheap; skips the deep noise floor).
    const snr = here - floor[i]!;
    if (!finite(snr) || snr < MIN_SNR_DB) continue;

    // Gate 2 — amplitude stationarity. Voice swings; a carrier is steady.
    const steadiness = steady[i]!;
    if (!finite(steadiness) || steadiness < MIN_STEADINESS) continue;

    // Gate 3 — prominence over the local saddle (carrier vs hump shoulder, and
    // the fix for CFAR self-masking on strong carriers).
    const prominence = saddleProminenceDb(spec, i, windowBins);
    if (prominence < MIN_PROMINENCE_DB) continue;

    // Gate 4 — narrowness. A carrier collapses within a couple of bins; a voice
    // hump stays above the −6 dB line for hundreds of Hz. The limit scales with
    // zoom so wide-span views don't reject carriers smeared across a few bins.
    const widthHz = widthAtDropHz(spec, i, here - WIDTH_DROP_DB, hzPerPixel);
    if (widthHz > maxCarrierWidthHz) continue;
    const occupiedWidthHz = occupiedShoulderWidthHz(spec, floor, i, hzPerPixel);
    if (occupiedWidthHz > occupiedShoulderLimitHz(maxCarrierWidthHz)) continue;

    const candidateCenterHz = binToHz(i, n, centerHz, hzPerPixel);

    // Gate 5 — never notch inside a protected digital segment (FT8/FT4): those
    // sub-bands are wall-to-wall steady carriers the operator wants to decode.
    if (protectedRanges.some((r) => candidateCenterHz >= r.lowHz && candidateCenterHz <= r.highHz)) {
      continue;
    }

    const candidate: AutoNotchCandidate = {
      centerHz: candidateCenterHz,
      widthHz: clampWidth(widthHz + 2 * NOTCH_PAD_HZ),
      snrDb: prominence,
      confidence: steadiness,
    };
    if (overlapsProtectedPassband(candidate.centerHz, candidate.widthHz, input.tunedPassband)) {
      continue;
    }
    if (!isCoveredByManualNotch(candidate, input.existingNotches ?? [])) {
      raw.push(candidate);
    }
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

// ── Diagnostics ──────────────────────────────────────────────────────────────
// "Log what's on the wire." When a carrier the operator can see is NOT being
// notched, these explain exactly which gate rejected it, with the live values,
// so thresholds are tuned from real data instead of guessed. Wired to a dev
// console hook in SignalIntelligenceController (window.zeusAnf).

export type AutoNotchGateReport = {
  freqHz: number;
  bin: number;
  isLocalMax: boolean;
  snrDb: number;
  prominenceDb: number;
  widthHz: number;
  occupiedWidthHz: number;
  maxCarrierWidthHz: number;
  maxOccupiedShoulderHz: number;
  steadiness: number;
  inProtectedRange: boolean;
  inTunedPassband: boolean;
  /** First gate that failed, or 'pass' when every gate is satisfied. */
  verdict: 'pass' | 'localMax' | 'snr' | 'stationarity' | 'prominence' | 'narrowness' | 'occupied' | 'passband' | 'protected' | 'nodata';
  thresholds: { minSnrDb: number; minProminenceDb: number; minSteadiness: number };
};

function freqToBin(freqHz: number, n: number, centerHz: number, hzPerPixel: number): number {
  return Math.round((freqHz - centerHz) / hzPerPixel + n / 2);
}

function reportAtBin(input: AutoNotchInput, i: number, n: number, centerHz: number): AutoNotchGateReport {
  const spec = input.spectrum!;
  const floor = input.floor!;
  const steady = input.stationarity!;
  const hzPerPixel = input.hzPerPixel;
  const windowBins = Math.max(2, Math.round(PROMINENCE_WINDOW_HZ / hzPerPixel));
  const maxCarrierWidthHz = Math.max(MAX_CARRIER_WIDTH_HZ, CARRIER_MAX_BINS * hzPerPixel);
  const here = spec[i]!;
  const isLocalMax = i > 0 && i < n - 1 && here >= spec[i - 1]! && here > spec[i + 1]!;
  const snr = here - floor[i]!;
  const steadiness = steady[i]!;
  const prominence = saddleProminenceDb(spec, i, windowBins);
  const widthHz = widthAtDropHz(spec, i, here - WIDTH_DROP_DB, hzPerPixel);
  const occupiedWidthHz = occupiedShoulderWidthHz(spec, floor, i, hzPerPixel);
  const maxOccupiedShoulderHz = occupiedShoulderLimitHz(maxCarrierWidthHz);
  const freqHz = binToHz(i, n, centerHz, hzPerPixel);
  const inProtectedRange = (input.protectedRanges ?? []).some(
    (r) => freqHz >= r.lowHz && freqHz <= r.highHz,
  );
  const inTunedPassband = overlapsProtectedPassband(freqHz, widthHz, input.tunedPassband);

  let verdict: AutoNotchGateReport['verdict'] = 'pass';
  if (!isLocalMax) verdict = 'localMax';
  else if (!(snr >= MIN_SNR_DB)) verdict = 'snr';
  else if (!(steadiness >= MIN_STEADINESS)) verdict = 'stationarity';
  else if (!(prominence >= MIN_PROMINENCE_DB)) verdict = 'prominence';
  else if (widthHz > maxCarrierWidthHz) verdict = 'narrowness';
  else if (occupiedWidthHz > maxOccupiedShoulderHz) verdict = 'occupied';
  else if (inTunedPassband) verdict = 'passband';
  else if (inProtectedRange) verdict = 'protected';

  return {
    freqHz,
    bin: i,
    isLocalMax,
    snrDb: Math.round(snr * 10) / 10,
    prominenceDb: Math.round(prominence * 10) / 10,
    widthHz: Math.round(widthHz),
    occupiedWidthHz: Math.round(occupiedWidthHz),
    maxCarrierWidthHz: Math.round(maxCarrierWidthHz),
    maxOccupiedShoulderHz: Math.round(maxOccupiedShoulderHz),
    steadiness: Math.round(steadiness * 100) / 100,
    inProtectedRange,
    inTunedPassband,
    verdict,
    thresholds: {
      minSnrDb: MIN_SNR_DB,
      minProminenceDb: MIN_PROMINENCE_DB,
      minSteadiness: MIN_STEADINESS,
    },
  };
}

/** Explain the gate decision for the bin nearest `freqHz`. Searches ±3 bins for
 *  the strongest local maximum so a click that lands a bin or two off the peak
 *  still reports the carrier. Returns null if the frame/estimator isn't ready. */
export function explainAutoNotchAt(input: AutoNotchInput, freqHz: number): AutoNotchGateReport | null {
  const spec = input.spectrum;
  const floor = input.floor;
  const steady = input.stationarity;
  const hzPerPixel = input.hzPerPixel;
  if (!spec || !floor || !steady || spec.length < 8) return null;
  const n = spec.length;
  if (floor.length !== n || steady.length !== n || !finite(hzPerPixel) || hzPerPixel <= 0) return null;
  const centerHz = Number(input.centerHz);
  if (!finite(freqHz) || !finite(centerHz)) return null;

  const target = freqToBin(freqHz, n, centerHz, hzPerPixel);
  if (target < 1 || target > n - 2) {
    return { ...reportAtBin(input, Math.max(1, Math.min(n - 2, target)), n, centerHz), verdict: 'nodata' };
  }
  let best = target;
  let bestVal = -Infinity;
  for (let i = Math.max(1, target - 3); i <= Math.min(n - 2, target + 3); i++) {
    if (spec[i]! > bestVal) {
      bestVal = spec[i]!;
      best = i;
    }
  }
  return reportAtBin(input, best, n, centerHz);
}

/** The strongest local maxima that FAILED a gate, with the reason — so you can
 *  see which gate is over-rejecting visible carriers. `limit` caps the list. */
export function explainAutoNotchRejections(input: AutoNotchInput, limit = 20): AutoNotchGateReport[] {
  const spec = input.spectrum;
  const floor = input.floor;
  const steady = input.stationarity;
  const hzPerPixel = input.hzPerPixel;
  if (!spec || !floor || !steady || spec.length < 8) return [];
  const n = spec.length;
  if (floor.length !== n || steady.length !== n || !finite(hzPerPixel) || hzPerPixel <= 0) return [];
  const centerHz = Number(input.centerHz);
  if (!finite(centerHz)) return [];

  const out: AutoNotchGateReport[] = [];
  for (let i = 1; i < n - 1; i++) {
    const here = spec[i]!;
    if (!finite(here) || !(here >= spec[i - 1]! && here > spec[i + 1]!)) continue;
    if (here - floor[i]! < 3) continue; // ignore deep-noise local maxima
    const r = reportAtBin(input, i, n, centerHz);
    if (r.verdict !== 'pass') out.push(r);
  }
  return out.sort((a, b) => b.prominenceDb - a.prominenceDb).slice(0, limit);
}
