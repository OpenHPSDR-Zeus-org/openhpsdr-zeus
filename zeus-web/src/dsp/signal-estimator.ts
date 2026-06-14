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

// Shared spectrum signal estimator.
//
// One per-bin noise-floor estimate feeds TWO operator features that both need
// to know "where is the noise and where are the signals":
//
//   • Signal Pop  — subtract the per-bin floor before the colormap so weak
//                   carriers leap off a flattened baseline (band tilt removed).
//   • Snap-to     — on click, search the bins near the cursor for the strongest
//                   carrier above the floor and tune exactly onto it.
//
// This is classical DSP, not ML. The floor is estimated SPATIALLY (across
// frequency) — an ordered-statistic CFAR estimate over nearby bins, lifted
// toward the noise mean and lightly smoothed in time. Spatial CFAR is the
// right model here: a bin's noise is read from its frequency neighbours, so
// even a STEADY carrier pops against the noise around it. A purely temporal
// per-bin tracker would instead converge up to a steady carrier and suppress
// it. It runs on the existing display pixels (panDb), so there is NO backend,
// native, or protocol change — and it is gated entirely off (zero per-frame
// cost) unless the operator turns Pop or Snap on.
//
// All of this is OPT-IN and OFF BY DEFAULT: first-connect behaviour is byte-for
// -byte the current panadapter/waterfall. The Pop dB window and snap tuning are
// display/UX surface — final values are the maintainer's call (CLAUDE.md).

import { create } from 'zustand';
import type { RxMode } from '../api/client';

// ── Floor-estimator tuning ──────────────────────────────────────────────────
// Spatial reference window, in Hz. A low percentile is taken over roughly
// ±(window/2) around each bin, so the window must be wider than the widest
// signal we want to pop (an SSB channel ≈ 2.7 kHz) to read the noise beside it.
// Unlike a true minimum, a low percentile is robust against one deep spectral
// null or notch dragging the whole local floor down and lighting nearby noise.
// Converted to bins per-frame using the live Hz/pixel, then clamped so extreme
// zoom levels stay sane.
const FLOOR_WINDOW_HZ = 6000;
const FLOOR_MIN_RADIUS_BINS = 6;
const FLOOR_MAX_RADIUS_BINS = 160;
const FLOOR_QUIET_PERCENTILE = 0.2;
const FLOOR_HIST_MIN_DB = -200;
const FLOOR_HIST_MAX_DB = 20;
const FLOOR_HIST_STEP_DB = 0.5;
const FLOOR_HIST_BINS = Math.round((FLOOR_HIST_MAX_DB - FLOOR_HIST_MIN_DB) / FLOOR_HIST_STEP_DB) + 1;
// The quiet percentile sits in the lower tail of the noise; lift it toward
// the noise mean so `raw − floor` centres noise near 0 dB rather than several
// dB up.
const NOISE_OFFSET_DB = 3;
// Light temporal smoothing of the spatial floor — kills frame-to-frame jitter
// in the minimum without lagging real noise-floor changes. 1.0 on a geometry
// reset so the first frame is already converged (no flat opening frame).
const FLOOR_TIME_EMA = 0.3;

// ── Pop display mapping (gate → compress → normalise) ───────────────────────
// enhanceInto turns `snr = raw − floor` (dB above the noise floor) into a 0..1
// display value the colormap maps directly (the renderers pass dbMin=0,dbMax=1
// while Pop is on):
//
//   1. GATE  — snr below popFloorDb is noise → 0 (dark).
//   2. SPAN  — popSpanDb is the snr that reaches full brightness; anything
//              stronger clips to 1. A SMALL span makes weak signals bright.
//   3. GAMMA — popGamma < 1 compresses the range so a 15 dB signal and a 70 dB
//              signal can BOTH read as bright at once. This is what stops two
//              strong carriers from owning the whole colour scale and burying
//              everything weaker in black.
//
// Defaults want on-air tuning by the maintainer; exposed in the store so the
// Settings > DSP "Signal Intelligence" surface can tune them live.
const DEFAULT_POP_FLOOR_DB = 3;
const DEFAULT_POP_SPAN_DB = 30;
const DEFAULT_POP_GAMMA = 0.5;
// Display-only persistence for weak ridges. The WDSP display frame rate can
// miss short CW/digital bursts or make weak voice traces flicker; a fast peak
// hold lets real signal energy leave a short visual afterimage while the gate
// still drives ordinary noise to black. This never feeds snap/peak detection.
const POP_PERSIST_DECAY = 0.82;
// Local contrast/ridge boost. After floor subtraction, narrow carriers and
// digital/CW traces are often only a few bins wide. Compare each bin's SNR
// with a short local positive-SNR mean; bins that stand above their immediate
// neighbourhood get a capped dB lift before the gamma map. Broad noise and
// wide raised baselines get little or no lift.
const POP_RIDGE_WINDOW_HZ = 1200;
const POP_RIDGE_MIN_RADIUS_BINS = 2;
const POP_RIDGE_MAX_RADIUS_BINS = 18;
const POP_RIDGE_BOOST = 0.35;
const POP_RIDGE_MAX_BOOST_DB = 8;
// Temporal coherence: real signals tend to persist in the same bin neighbourhood
// for multiple display frames, while random FFT speckles and single-row QRN
// hits do not. Track a 0..1 confidence per bin and let it drive persistence
// and a small display boost. Current-frame energy still shows immediately;
// coherence mostly decides whether that energy should linger and glow.
const COHERENCE_SNR_FULL_DB = 22;
const COHERENCE_DECAY = 0.88;
const COHERENCE_HOLD_GATE = 0.45;
const COHERENCE_VISUAL_BOOST_DB = 4;

// ── Snap search ─────────────────────────────────────────────────────────────
// Look this far either side of the click for a carrier, and only snap to bins
// at least this many dB above the local floor (else there's nothing to grab).
const SNAP_RADIUS_HZ = 4000;
const SNAP_MIN_SNR_DB = 6;
// Mode-aware tune placement: walk out from the carrier peak while the spectrum
// stays this far above the floor to find the signal's energy extent (its
// "transmitted bandwidth"). A few sub-threshold bins are tolerated so intra-
// voice dips don't truncate the channel; the walk is capped so it can't run
// across the whole band on a noisy day.
const SNAP_EDGE_SNR_DB = 6;
const SNAP_EDGE_GAP_BINS = 3;
const SNAP_MAX_SIGNAL_HZ = 8000;
// Snap history (waterfall memory): when no LIVE signal sits near the click,
// snap consults a slow-decaying max-hold of recent signal energy — the same
// persistent/intermittent carriers the operator sees streaking down the
// waterfall. A bin only enters the memory once it crosses SNAP_HISTORY_GATE_DB
// (a real-carrier gate, so noise speckle never pollutes history); it then fades
// by SNAP_HISTORY_DROP_DB per frame, lingering for the few seconds it stays
// visible on the waterfall before dropping back below the snap threshold.
const SNAP_HISTORY_GATE_DB = 8;
const SNAP_HISTORY_DROP_DB = 0.4;

// ── Peak detection (CFAR-style markers) ─────────────────────────────────────
// A bin is a marked peak when it is a local maximum at least this many dB above
// its spatial floor. A little stricter than the snap threshold so the markers
// flag real carriers, not every noise bump. Near-duplicates inside the spacing
// (skirts of one wide signal) collapse to the strongest; total markers capped.
const PEAK_MIN_SNR_DB = 8;
const PEAK_MIN_SPACING_BINS = 4;
const PEAK_MAX_COUNT = 48;
// SNR at which a marker reaches full opacity (alpha scales 0..1 up to this).
const PEAK_FULL_SNR_DB = 40;

const STORAGE_KEY = 'zeus.enhance';

export type SignalEnhanceProfileId = 'balanced' | 'dx' | 'cw' | 'digital' | 'voice' | 'contest' | 'custom';
export type SignalEnhancePresetId = Exclude<SignalEnhanceProfileId, 'custom'>;

export type SignalEnhanceTuning = {
  /** Operator-facing profile label. `custom` means at least one tunable moved. */
  profileId: SignalEnhanceProfileId;
  /** Pop noise gate: snr (dB above floor) below this maps to black. */
  popFloorDb: number;
  /** Pop full-brightness snr (dB above floor); stronger signals clip. */
  popSpanDb: number;
  /** Pop compression exponent (<1 lifts weak signals relative to strong). */
  popGamma: number;
  /** Confidence threshold required before signal energy can persist/glow. */
  coherenceHoldGate: number;
  /** Display-only dB lift for repeated/neighbour-supported signal energy. */
  coherenceBoostDb: number;
  /** Local narrow-ridge contrast gain for CW/digital/carrier traces. */
  ridgeBoost: number;
  /** Cap for ridge contrast gain, in dB. */
  ridgeMaxBoostDb: number;
  /** Default carrier search radius used by direct snap helpers. */
  snapRadiusHz: number;
  /** Minimum dB over local floor required for snap-to-signal grabs. */
  snapMinSnrDb: number;
  /** Minimum dB over local floor required for snap marker peaks. */
  peakMinSnrDb: number;
};

export type SignalEnhanceTuningPatch = Partial<SignalEnhanceTuning>;

export type SignalEnhanceState = SignalEnhanceTuning & {
  /** Per-bin noise-floor subtraction on the panadapter + waterfall. */
  popEnabled: boolean;
  /** Snap-to-signal on panadapter/waterfall click. */
  snapEnabled: boolean;
  /** Follow the receiver mode with the matching profile (CW, digital, voice). */
  autoProfileEnabled: boolean;
  setPopEnabled: (v: boolean) => void;
  setSnapEnabled: (v: boolean) => void;
  togglePop: () => void;
  toggleSnap: () => void;
  setSignalEnhanceTuning: (patch: SignalEnhanceTuningPatch) => void;
  applySignalEnhanceProfile: (profileId: SignalEnhancePresetId) => void;
  applySignalEnhanceAutoProfile: (profileId: SignalEnhancePresetId) => void;
  applySignalEnhanceModeProfile: (mode: RxMode) => void;
  setSignalEnhanceAutoProfile: (enabled: boolean, mode?: RxMode) => void;
  resetSignalEnhanceTuning: () => void;
};

type SignalEnhancePersisted = Pick<SignalEnhanceState, 'popEnabled' | 'snapEnabled' | 'autoProfileEnabled'> & SignalEnhanceTuning;
type SignalEnhanceTuningValues = Omit<SignalEnhanceTuning, 'profileId'>;

export const SIGNAL_ENHANCE_PROFILE_ORDER: readonly SignalEnhancePresetId[] = [
  'balanced',
  'dx',
  'cw',
  'digital',
  'voice',
  'contest',
];

export const SIGNAL_ENHANCE_PROFILES: Record<SignalEnhancePresetId, SignalEnhanceTuningValues> = {
  balanced: {
    popFloorDb: DEFAULT_POP_FLOOR_DB,
    popSpanDb: DEFAULT_POP_SPAN_DB,
    popGamma: DEFAULT_POP_GAMMA,
    coherenceHoldGate: COHERENCE_HOLD_GATE,
    coherenceBoostDb: COHERENCE_VISUAL_BOOST_DB,
    ridgeBoost: POP_RIDGE_BOOST,
    ridgeMaxBoostDb: POP_RIDGE_MAX_BOOST_DB,
    snapRadiusHz: SNAP_RADIUS_HZ,
    snapMinSnrDb: SNAP_MIN_SNR_DB,
    peakMinSnrDb: PEAK_MIN_SNR_DB,
  },
  dx: {
    popFloorDb: 2,
    popSpanDb: 24,
    popGamma: 0.42,
    coherenceHoldGate: 0.38,
    coherenceBoostDb: 5.5,
    ridgeBoost: 0.5,
    ridgeMaxBoostDb: 10,
    snapRadiusHz: 5000,
    snapMinSnrDb: 5,
    peakMinSnrDb: 7,
  },
  cw: {
    popFloorDb: 2.5,
    popSpanDb: 22,
    popGamma: 0.46,
    coherenceHoldGate: 0.4,
    coherenceBoostDb: 5,
    ridgeBoost: 0.65,
    ridgeMaxBoostDb: 11,
    snapRadiusHz: 2500,
    snapMinSnrDb: 5,
    peakMinSnrDb: 6.5,
  },
  digital: {
    popFloorDb: 2.5,
    popSpanDb: 26,
    popGamma: 0.48,
    coherenceHoldGate: 0.42,
    coherenceBoostDb: 4.5,
    ridgeBoost: 0.55,
    ridgeMaxBoostDb: 10,
    snapRadiusHz: 3500,
    snapMinSnrDb: 5.5,
    peakMinSnrDb: 7,
  },
  voice: {
    popFloorDb: 3.5,
    popSpanDb: 34,
    popGamma: 0.55,
    coherenceHoldGate: 0.48,
    coherenceBoostDb: 3.5,
    ridgeBoost: 0.32,
    ridgeMaxBoostDb: 7,
    snapRadiusHz: 5000,
    snapMinSnrDb: 6.5,
    peakMinSnrDb: 8.5,
  },
  contest: {
    popFloorDb: 4,
    popSpanDb: 28,
    popGamma: 0.5,
    coherenceHoldGate: 0.55,
    coherenceBoostDb: 3,
    ridgeBoost: 0.45,
    ridgeMaxBoostDb: 8,
    snapRadiusHz: 2200,
    snapMinSnrDb: 7,
    peakMinSnrDb: 9,
  },
};

const DEFAULT_TUNING: SignalEnhanceTuning = {
  profileId: 'balanced',
  ...SIGNAL_ENHANCE_PROFILES.balanced,
};

export function signalEnhanceProfileForMode(mode: RxMode): SignalEnhancePresetId {
  switch (mode) {
    case 'CWU':
    case 'CWL':
      return 'cw';
    case 'DIGU':
    case 'DIGL':
      return 'digital';
    case 'LSB':
    case 'USB':
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return 'voice';
    default:
      return 'balanced';
  }
}

export type SignalEnhanceSceneInput = {
  mode: RxMode;
  spectrum: Float32Array | null;
  floor: Float32Array | null;
  hzPerPixel: number;
};

export type SignalEnhanceScene = {
  profileId: SignalEnhancePresetId;
  baseProfileId: SignalEnhancePresetId;
  peakCount: number;
  peaksPer10Khz: number;
  occupiedRatio: number;
  maxSnrDb: number;
};

const SCENE_PEAK_GATE_DB = 8;
const SCENE_BUSY_PEAKS_PER_10KHZ = 3.5;
const SCENE_BUSY_OCCUPIED_RATIO = 0.14;
const SCENE_DX_MAX_PEAKS_PER_10KHZ = 1.6;
const SCENE_DX_MAX_SNR_DB = 24;

/** Recommend a Signal Intelligence profile from current mode + spectrum scene.
 *  The mode remains the primary constraint: CW and digital keep their narrow
 *  ridge-heavy profiles. Voice-like modes can adapt between Voice, DX, and
 *  Contest by looking at CFAR SNR peaks and occupied-bin density. */
export function recommendSignalEnhanceScene(input: SignalEnhanceSceneInput): SignalEnhanceScene {
  const baseProfileId = signalEnhanceProfileForMode(input.mode);
  const spec = input.spectrum;
  const f = input.floor;
  if (spec === null || f === null || spec.length < 3 || f.length !== spec.length || input.hzPerPixel <= 0) {
    return {
      profileId: baseProfileId,
      baseProfileId,
      peakCount: 0,
      peaksPer10Khz: 0,
      occupiedRatio: 0,
      maxSnrDb: 0,
    };
  }

  let peakCount = 0;
  let occupied = 0;
  let maxSnrDb = 0;
  for (let i = 1; i < spec.length - 1; i++) {
    const snr = spec[i]! - f[i]!;
    if (snr >= SCENE_PEAK_GATE_DB) occupied++;
    if (snr > maxSnrDb) maxSnrDb = snr;
    if (snr >= SCENE_PEAK_GATE_DB && spec[i]! >= spec[i - 1]! && spec[i]! > spec[i + 1]!) {
      peakCount++;
    }
  }
  const span10Khz = Math.max(1, (spec.length * input.hzPerPixel) / 10_000);
  const peaksPer10Khz = peakCount / span10Khz;
  const occupiedRatio = occupied / spec.length;

  let profileId = baseProfileId;
  if (baseProfileId === 'voice') {
    if (peaksPer10Khz >= SCENE_BUSY_PEAKS_PER_10KHZ || occupiedRatio >= SCENE_BUSY_OCCUPIED_RATIO) {
      profileId = 'contest';
    } else if (peakCount > 0 && peaksPer10Khz <= SCENE_DX_MAX_PEAKS_PER_10KHZ && maxSnrDb <= SCENE_DX_MAX_SNR_DB) {
      profileId = 'dx';
    }
  }

  return { profileId, baseProfileId, peakCount, peaksPer10Khz, occupiedRatio, maxSnrDb };
}

function clampFinite(v: unknown, min: number, max: number, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? Math.max(min, Math.min(max, v)) : fallback;
}

function isProfileId(v: unknown): v is SignalEnhanceProfileId {
  return (
    v === 'balanced' ||
    v === 'dx' ||
    v === 'cw' ||
    v === 'digital' ||
    v === 'voice' ||
    v === 'contest' ||
    v === 'custom'
  );
}

function normalizeTuning(raw: SignalEnhanceTuningPatch = {}, fallback: SignalEnhanceTuning = DEFAULT_TUNING): SignalEnhanceTuning {
  const profileId = isProfileId(raw.profileId) ? raw.profileId : fallback.profileId;
  const base = profileId === 'custom' ? fallback : { profileId, ...SIGNAL_ENHANCE_PROFILES[profileId] };
  return {
    profileId,
    popFloorDb: clampFinite(raw.popFloorDb, 0, 12, base.popFloorDb),
    popSpanDb: clampFinite(raw.popSpanDb, 12, 60, base.popSpanDb),
    popGamma: clampFinite(raw.popGamma, 0.3, 1.2, base.popGamma),
    coherenceHoldGate: clampFinite(raw.coherenceHoldGate, 0.2, 0.8, base.coherenceHoldGate),
    coherenceBoostDb: clampFinite(raw.coherenceBoostDb, 0, 8, base.coherenceBoostDb),
    ridgeBoost: clampFinite(raw.ridgeBoost, 0, 0.8, base.ridgeBoost),
    ridgeMaxBoostDb: clampFinite(raw.ridgeMaxBoostDb, 0, 12, base.ridgeMaxBoostDb),
    snapRadiusHz: clampFinite(raw.snapRadiusHz, 500, 12_000, base.snapRadiusHz),
    snapMinSnrDb: clampFinite(raw.snapMinSnrDb, 3, 16, base.snapMinSnrDb),
    peakMinSnrDb: clampFinite(raw.peakMinSnrDb, 4, 20, base.peakMinSnrDb),
  };
}

function readPersisted(): SignalEnhancePersisted {
  try {
    if (typeof localStorage === 'undefined') {
      return { popEnabled: false, snapEnabled: false, autoProfileEnabled: false, ...DEFAULT_TUNING };
    }
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { popEnabled: false, snapEnabled: false, autoProfileEnabled: false, ...DEFAULT_TUNING };
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    return {
      popEnabled: parsed.popEnabled === true,
      snapEnabled: parsed.snapEnabled === true,
      autoProfileEnabled: parsed.autoProfileEnabled === true,
      ...normalizeTuning(parsed as SignalEnhanceTuningPatch),
    };
  } catch {
    return { popEnabled: false, snapEnabled: false, autoProfileEnabled: false, ...DEFAULT_TUNING };
  }
}

function persist(s: SignalEnhancePersisted): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      popEnabled: s.popEnabled,
      snapEnabled: s.snapEnabled,
      autoProfileEnabled: s.autoProfileEnabled,
      profileId: s.profileId,
      popFloorDb: s.popFloorDb,
      popSpanDb: s.popSpanDb,
      popGamma: s.popGamma,
      coherenceHoldGate: s.coherenceHoldGate,
      coherenceBoostDb: s.coherenceBoostDb,
      ridgeBoost: s.ridgeBoost,
      ridgeMaxBoostDb: s.ridgeMaxBoostDb,
      snapRadiusHz: s.snapRadiusHz,
      snapMinSnrDb: s.snapMinSnrDb,
      peakMinSnrDb: s.peakMinSnrDb,
    }));
  } catch {
    // quota / private mode — in-memory state is still the source of truth.
  }
}

const persisted = readPersisted();

export const useSignalEnhanceStore = create<SignalEnhanceState>((set, get) => ({
  popEnabled: persisted.popEnabled,
  snapEnabled: persisted.snapEnabled,
  autoProfileEnabled: persisted.autoProfileEnabled,
  profileId: persisted.profileId,
  popFloorDb: persisted.popFloorDb,
  popSpanDb: persisted.popSpanDb,
  popGamma: persisted.popGamma,
  coherenceHoldGate: persisted.coherenceHoldGate,
  coherenceBoostDb: persisted.coherenceBoostDb,
  ridgeBoost: persisted.ridgeBoost,
  ridgeMaxBoostDb: persisted.ridgeMaxBoostDb,
  snapRadiusHz: persisted.snapRadiusHz,
  snapMinSnrDb: persisted.snapMinSnrDb,
  peakMinSnrDb: persisted.peakMinSnrDb,
  setPopEnabled: (popEnabled) => {
    set({ popEnabled });
    if (!popEnabled) resetSignalHold();
    persist(get());
  },
  setSnapEnabled: (snapEnabled) => {
    set({ snapEnabled });
    persist(get());
  },
  togglePop: () => {
    const popEnabled = !get().popEnabled;
    set({ popEnabled });
    if (!popEnabled) resetSignalHold();
    persist(get());
  },
  toggleSnap: () => {
    const snapEnabled = !get().snapEnabled;
    set({ snapEnabled });
    persist(get());
  },
  setSignalEnhanceTuning: (patch) => {
    const current = get();
    const next = normalizeTuning({ ...current, ...patch, profileId: patch.profileId ?? 'custom' }, current);
    set({ ...next, autoProfileEnabled: false });
    resetSignalHold();
    persist(get());
  },
  applySignalEnhanceProfile: (profileId) => {
    const next = normalizeTuning({ profileId, ...SIGNAL_ENHANCE_PROFILES[profileId] });
    set({ ...next, autoProfileEnabled: false });
    resetSignalHold();
    persist(get());
  },
  applySignalEnhanceAutoProfile: (profileId) => {
    const next = normalizeTuning({ profileId, ...SIGNAL_ENHANCE_PROFILES[profileId] });
    set({ ...next, autoProfileEnabled: true });
    resetSignalHold();
    persist(get());
  },
  applySignalEnhanceModeProfile: (mode) => {
    const profileId = signalEnhanceProfileForMode(mode);
    get().applySignalEnhanceAutoProfile(profileId);
  },
  setSignalEnhanceAutoProfile: (autoProfileEnabled, mode) => {
    if (autoProfileEnabled && mode) {
      const profileId = signalEnhanceProfileForMode(mode);
      get().applySignalEnhanceAutoProfile(profileId);
    } else {
      set({ autoProfileEnabled });
      resetSignalHold();
      persist(get());
    }
  },
  resetSignalEnhanceTuning: () => {
    const next = normalizeTuning({ profileId: 'balanced', ...SIGNAL_ENHANCE_PROFILES.balanced });
    set({ ...next, autoProfileEnabled: false });
    resetSignalHold();
    persist(get());
  },
}));

// ── Per-bin floor estimator (module singleton) ──────────────────────────────
// Singleton, not per-component: the panadapter and waterfall must enhance off
// the SAME floor, and a click handler needs it on demand. Driven once per frame
// from display-store.pushFrame so both surfaces see an already-updated floor.

let floor: Float32Array | null = null; // published, time-smoothed floor
let quietRef: Float32Array | null = null; // per-frame low-percentile spatial reference
let signalHold: Float32Array | null = null; // display-only SNR peak hold
let signalConfidence: Float32Array | null = null; // 0..1 temporal/neighbour confidence
let previousSnr: Float32Array | null = null; // previous-frame SNR for coherence
let ridgeMean: Float32Array | null = null; // display-only local positive-SNR mean
let snapHistorySnr: Float32Array | null = null; // slow-decay SNR max-hold for snap history
let snapHistorySpec: Float32Array | null = null; // reusable floor+history reconstruction
let hist: Int32Array | null = null; // quantized dB histogram for ordered-statistic CFAR
let geomKey = '';
let lastHzPerPixel = 0;

/** A floor estimate is only valid for the geometry it was built on. Width and
 *  Hz/pixel changes (zoom, sample rate) re-scale the window and bin layout, so
 *  re-converge in one frame (EMA=1). A plain retune does NOT reset — the
 *  spatial estimate is frame-local and needs no per-bin history. */
function makeGeomKey(width: number, hzPerPixel: number): string {
  return `${width}:${hzPerPixel}`;
}

type EstimatorFrame = {
  panDb: Float32Array | null;
  panValid: boolean;
  width: number;
  hzPerPixel: number;
};

// Ref-counted estimator consumers. A surface that needs the floor/detection
// while global Pop and Snap are both off (e.g. the Bandwidth Filter panel, which
// wants live signal awareness whenever it is open) registers one. The estimator
// runs while any consumer is active; closing the panel (and Pop/Snap off) drops
// the count back to zero and the per-frame cost disappears.
let estimatorConsumers = 0;

/** Keep the floor estimator running while this consumer is registered, even if
 *  global Pop/Snap are off. Returns a release function (idempotent). */
export function registerEstimatorConsumer(): () => void {
  estimatorConsumers += 1;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    estimatorConsumers = Math.max(0, estimatorConsumers - 1);
  };
}

/** Update the floor estimate for this frame — but only when an operator feature
 *  needs it. When Pop, Snap, and all registered consumers are absent this
 *  returns immediately, so the feature carries no cost until switched on. Call
 *  before notifying subscribers so the same-frame enhance sees this frame's
 *  floor. */
export function maybeUpdateEstimator(f: EstimatorFrame): void {
  const st = useSignalEnhanceStore.getState();
  if (!st.popEnabled && !st.snapEnabled && estimatorConsumers === 0) return;
  if (!f.panValid || !f.panDb || f.panDb.length === 0) return;
  updateFloor(f.panDb, f.hzPerPixel, makeGeomKey(f.width, f.hzPerPixel));
}

function histIndex(db: number): number {
  if (!Number.isFinite(db)) return 0;
  const idx = Math.round((db - FLOOR_HIST_MIN_DB) / FLOOR_HIST_STEP_DB);
  return idx < 0 ? 0 : idx >= FLOOR_HIST_BINS ? FLOOR_HIST_BINS - 1 : idx;
}

function histDb(idx: number): number {
  return FLOOR_HIST_MIN_DB + idx * FLOOR_HIST_STEP_DB;
}

/** Ordered-statistic CFAR floor over ±radius bins, written into `out`.
 *  The low percentile behaves like a quiet-neighbour detector but is much less
 *  sensitive than a raw minimum to deep spectral holes, notches, and one-bin
 *  FFT outliers. Histogram quantisation keeps the per-frame cost linear in
 *  width and stable at high zoom. */
function quietPercentile(src: Float32Array, radius: number, out: Float32Array): void {
  const n = src.length;
  if (hist === null) hist = new Int32Array(FLOOR_HIST_BINS);
  const h = hist;
  h.fill(0);
  let lo = 0;
  let hi = -1;
  let count = 0;

  const add = (i: number) => {
    const idx = histIndex(src[i]!);
    h[idx] = h[idx]! + 1;
    count++;
  };
  const remove = (i: number) => {
    const idx = histIndex(src[i]!);
    h[idx] = h[idx]! - 1;
    count--;
  };
  const percentile = () => {
    const rank = Math.max(1, Math.ceil(count * FLOOR_QUIET_PERCENTILE));
    let seen = 0;
    for (let b = 0; b < FLOOR_HIST_BINS; b++) {
      seen += h[b]!;
      if (seen >= rank) return histDb(b);
    }
    return histDb(FLOOR_HIST_BINS - 1);
  };

  for (let i = 0; i < n; i++) {
    const nextLo = Math.max(0, i - radius);
    const nextHi = Math.min(n - 1, i + radius);
    while (hi < nextHi) add(++hi);
    while (lo < nextLo) remove(lo++);
    out[i] = percentile();
  }
}

function updateFloor(spec: Float32Array, hzPerPixel: number, key: string): void {
  const n = spec.length;
  const reset = floor === null || floor.length !== n || key !== geomKey;
  if (floor === null || floor.length !== n) floor = new Float32Array(n);
  if (quietRef === null || quietRef.length !== n) quietRef = new Float32Array(n);
  if (reset) {
    resetSignalHold();
    resetSnapHistory();
  }

  let radius = hzPerPixel > 0 ? Math.round(FLOOR_WINDOW_HZ / hzPerPixel / 2) : FLOOR_MIN_RADIUS_BINS;
  radius = Math.max(FLOOR_MIN_RADIUS_BINS, Math.min(FLOOR_MAX_RADIUS_BINS, radius));
  quietPercentile(spec, radius, quietRef);

  const f = floor;
  const ref = quietRef;
  const a = reset ? 1 : FLOOR_TIME_EMA;
  for (let i = 0; i < n; i++) {
    const target = ref[i]! + NOISE_OFFSET_DB;
    f[i] = reset ? target : f[i]! + a * (target - f[i]!);
  }
  geomKey = key;
  lastHzPerPixel = hzPerPixel;
  updateSignalHold(spec);
  updateSnapHistory(spec);
}

function updateSignalHold(spec: Float32Array): void {
  const st = useSignalEnhanceStore.getState();
  const f = floor;
  const n = spec.length;
  if (!st.popEnabled || f === null || f.length !== n) {
    resetSignalHold();
    return;
  }
  if (signalHold === null || signalHold.length !== n) signalHold = new Float32Array(n);
  if (signalConfidence === null || signalConfidence.length !== n) signalConfidence = new Float32Array(n);
  if (previousSnr === null || previousSnr.length !== n) previousSnr = new Float32Array(n);
  const hold = signalHold;
  const conf = signalConfidence;
  const prev = previousSnr;
  const gate = st.popFloorDb;
  const coherenceHoldGate = st.coherenceHoldGate;
  for (let i = 0; i < n; i++) {
    const snr = spec[i]! - f[i]!;
    const leftSnr = i > 0 ? spec[i - 1]! - f[i - 1]! : -Infinity;
    const rightSnr = i + 1 < n ? spec[i + 1]! - f[i + 1]! : -Infinity;
    const neighbourSupported = leftSnr >= gate || rightSnr >= gate;
    const previousSupported = prev[i]! >= gate;
    const stable = previousSupported && Math.abs(snr - prev[i]!) <= 10;
    const snrNorm = Math.max(0, Math.min(1, (snr - gate) / COHERENCE_SNR_FULL_DB));
    const targetConfidence = snr >= gate
      ? Math.min(
        1,
        0.10 + 0.30 * snrNorm +
        (neighbourSupported ? 0.24 : 0) +
        (previousSupported ? 0.20 : 0) +
        (stable ? 0.10 : 0),
      )
      : 0;
    conf[i] = targetConfidence > conf[i]! * COHERENCE_DECAY
      ? targetConfidence
      : conf[i]! * COHERENCE_DECAY;

    const target = snr >= gate ? snr : 0;
    const holdDecay = conf[i]! >= coherenceHoldGate
      ? POP_PERSIST_DECAY
      : POP_PERSIST_DECAY * 0.55;
    const decayed = hold[i]! * holdDecay;
    const next = target > decayed ? target : decayed;
    hold[i] = next >= gate && conf[i]! >= coherenceHoldGate ? next : 0;
    prev[i] = snr;
  }
}

function resetSignalHold(): void {
  signalHold = null;
  signalConfidence = null;
  previousSnr = null;
}

/** Per-frame waterfall memory for snap. Runs only while Snap is enabled (it is
 *  independent of Pop, which owns signalHold). Each bin that crosses the
 *  real-carrier gate is remembered at its current SNR; bins below the gate fade
 *  by a fixed dB per frame, so a carrier that scrolls off the live FFT but is
 *  still visible on the waterfall stays grabbable for a few seconds. */
function updateSnapHistory(spec: Float32Array): void {
  const st = useSignalEnhanceStore.getState();
  const f = floor;
  const n = spec.length;
  if (!st.snapEnabled || f === null || f.length !== n) {
    resetSnapHistory();
    return;
  }
  if (snapHistorySnr === null || snapHistorySnr.length !== n) snapHistorySnr = new Float32Array(n);
  const h = snapHistorySnr;
  for (let i = 0; i < n; i++) {
    const snr = spec[i]! - f[i]!;
    if (snr >= SNAP_HISTORY_GATE_DB) {
      h[i] = snr;
    } else {
      const decayed = h[i]! - SNAP_HISTORY_DROP_DB;
      h[i] = decayed > 0 ? decayed : 0;
    }
  }
}

function resetSnapHistory(): void {
  snapHistorySnr = null;
  snapHistorySpec = null;
}

/** A pseudo-spectrum (live floor + remembered SNR) for the snap history fallback,
 *  or null when nothing has been remembered yet. Reuses the snap cluster/edge
 *  logic: bins with held energy read above the floor exactly as a live signal
 *  would, so computeSnapToLineHz finds and edge-aligns them. Read-only. */
export function getSnapHistorySpectrum(): Float32Array | null {
  const f = floor;
  const h = snapHistorySnr;
  if (f === null || h === null || h.length !== f.length) return null;
  const n = f.length;
  if (snapHistorySpec === null || snapHistorySpec.length !== n) snapHistorySpec = new Float32Array(n);
  const out = snapHistorySpec;
  let any = false;
  for (let i = 0; i < n; i++) {
    out[i] = f[i]! + h[i]!;
    if (h[i]! > 0) any = true;
  }
  return any ? out : null;
}

/** Current per-bin floor, or null before the first frame. Read-only — callers
 *  must not mutate it. */
export function getNoiseFloor(): Float32Array | null {
  return floor;
}

/** Current 0..1 signal confidence map, or null before Pop has accumulated it.
 *  Read-only. Values near 1 mean the bin has repeated/neighbour-supported
 *  signal energy; values near 0 are noise or one-frame speckles. */
export function getSignalConfidence(): Float32Array | null {
  return signalConfidence;
}

/** Reset the estimator (e.g. on disconnect). Next frame re-seeds. */
export function resetEstimator(): void {
  floor = null;
  quietRef = null;
  signalHold = null;
  signalConfidence = null;
  previousSnr = null;
  ridgeMean = null;
  snapHistorySnr = null;
  snapHistorySpec = null;
  geomKey = '';
  lastHzPerPixel = 0;
}

function computeLocalMeanPositiveSnr(raw: Float32Array, f: Float32Array, hold: Float32Array | null, out: Float32Array): void {
  const n = raw.length;
  let radius = lastHzPerPixel > 0 ? Math.round(POP_RIDGE_WINDOW_HZ / lastHzPerPixel / 2) : POP_RIDGE_MIN_RADIUS_BINS;
  radius = Math.max(POP_RIDGE_MIN_RADIUS_BINS, Math.min(POP_RIDGE_MAX_RADIUS_BINS, radius));

  const snrAt = (i: number): number => {
    const live = raw[i]! - f[i]!;
    const held = hold ? hold[i]! : 0;
    const snr = live > held ? live : held;
    return snr > 0 ? snr : 0;
  };

  let lo = 0;
  let hi = -1;
  let sum = 0;
  for (let i = 0; i < n; i++) {
    const nextLo = Math.max(0, i - radius);
    const nextHi = Math.min(n - 1, i + radius);
    while (hi < nextHi) sum += snrAt(++hi);
    while (lo < nextLo) sum -= snrAt(lo++);
    out[i] = sum / (hi - lo + 1);
  }
}

/** Map the spectrum to a 0..1 Pop display value per bin: subtract the floor,
 *  gate the noise, then compress so weak and strong signals are both visible
 *  (see the "Pop display mapping" notes above). The renderers pass dbMin=0,
 *  dbMax=1 while Pop is on, so the colormap consumes this directly. Outputs 0
 *  (dark) when no floor exists yet — only the first frame after a reset, which
 *  the estimator seeds before subscribers run. `out` must match `raw`'s length. */
export function enhanceInto(raw: Float32Array, out: Float32Array): void {
  const n = raw.length;
  const f = floor;
  if (f === null || f.length !== n) {
    out.fill(0);
    return;
  }
  const st = useSignalEnhanceStore.getState();
  const gate = st.popFloorDb;
  const span = st.popSpanDb > 1 ? st.popSpanDb : 1;
  const gamma = st.popGamma;
  const coherenceHoldGate = st.coherenceHoldGate;
  const coherenceBoostDb = st.coherenceBoostDb;
  const ridgeBoost = st.ridgeBoost;
  const ridgeMaxBoostDb = st.ridgeMaxBoostDb;
  const hold = signalHold && signalHold.length === n ? signalHold : null;
  const conf = signalConfidence && signalConfidence.length === n ? signalConfidence : null;
  if (ridgeMean === null || ridgeMean.length !== n) ridgeMean = new Float32Array(n);
  computeLocalMeanPositiveSnr(raw, f, hold, ridgeMean);
  const mean = ridgeMean;
  for (let i = 0; i < n; i++) {
    const liveSnr = raw[i]! - f[i]!;
    const confidence = conf ? conf[i]! : 0;
    const heldSnr = hold && confidence >= coherenceHoldGate
      ? hold[i]! * (0.45 + 0.55 * confidence)
      : 0;
    let snr = heldSnr > liveSnr ? heldSnr : liveSnr;
    const contrast = snr - mean[i]!;
    if (contrast > 0) snr += Math.min(ridgeMaxBoostDb, contrast * ridgeBoost);
    if (snr >= gate) {
      const ridgeWeight = contrast <= 0 ? 0.15 : Math.min(1, contrast / 12);
      snr += confidence * coherenceBoostDb * ridgeWeight;
    }
    let v = (snr - gate) / span;
    v = v < 0 ? 0 : v > 1 ? 1 : v;
    out[i] = gamma === 1 ? v : Math.pow(v, gamma);
  }
}

/** Find the strongest carrier near a clicked frequency and return its exact
 *  bin-center frequency, or null if nothing rises far enough above the floor.
 *
 *  Pure: caller supplies the live spectrum + geometry (panadapter store) and we
 *  use the shared floor. Bypasses the 500 Hz tune grid — snap lands on the real
 *  carrier, not the nearest round number. */
export function findPeakHz(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  clickHz: number,
): number | null {
  const n = spec.length;
  if (n < 3 || hzPerPixel <= 0) return null;
  const f = floor;
  const st = useSignalEnhanceStore.getState();
  const half = n / 2;
  const clickBin = Math.round((clickHz - centerHz) / hzPerPixel + half);
  const radius = Math.max(2, Math.round(st.snapRadiusHz / hzPerPixel));
  const lo = Math.max(1, clickBin - radius);
  const hi = Math.min(n - 2, clickBin + radius);
  let bestBin = -1;
  let bestVal = -Infinity;
  for (let i = lo; i <= hi; i++) {
    const v = spec[i]!;
    const fl = f && f.length === n ? f[i]! : -200;
    if (v < fl + coherentThreshold(st.snapMinSnrDb, i, n)) continue;
    // Local maximum so we land on the carrier crest, not its skirt.
    if (v >= spec[i - 1]! && v >= spec[i + 1]! && v > bestVal) {
      bestVal = v;
      bestBin = i;
    }
  }
  if (bestBin < 0) return null;
  return centerHz + (bestBin - half) * hzPerPixel;
}

export type DetectedPeak = {
  /** Absolute bin-centre frequency of the peak. */
  hz: number;
  /** dB above the local noise floor (used for marker opacity). */
  snrDb: number;
};

function confidenceAt(index: number, n: number): number {
  return signalConfidence && signalConfidence.length === n ? signalConfidence[index]! : 0;
}

function coherentThreshold(baseDb: number, index: number, n: number): number {
  const confidence = confidenceAt(index, n);
  return confidence >= 0.65 ? baseDb - 2 : baseDb;
}

/** Detect carriers across the whole span for the snap-target markers. Returns
 *  local maxima at least PEAK_MIN_SNR_DB above their spatial floor, de-duplicated
 *  to the strongest within PEAK_MIN_SPACING_BINS and capped at PEAK_MAX_COUNT
 *  (strongest first). Empty until a floor exists. Pure: caller passes the live
 *  spectrum + geometry. */
export function detectPeaks(spec: Float32Array, centerHz: number, hzPerPixel: number): DetectedPeak[] {
  const n = spec.length;
  const f = floor;
  if (n < 3 || hzPerPixel <= 0 || f === null || f.length !== n) return [];
  const st = useSignalEnhanceStore.getState();
  const half = n / 2;
  const found: Array<{ bin: number; snrDb: number }> = [];
  for (let i = 1; i < n - 1; i++) {
    const v = spec[i]!;
    const snr = v - f[i]!;
    if (snr < coherentThreshold(st.peakMinSnrDb, i, n)) continue;
    // `>=` left / `>` right accepts a flat top once (at its right edge) rather
    // than emitting a marker for every bin of the plateau.
    if (v >= spec[i - 1]! && v > spec[i + 1]!) {
      found.push({ bin: i, snrDb: snr + confidenceAt(i, n) * 2 });
    }
  }
  // Strongest first, then greedily accept peaks that clear the spacing — so the
  // skirts of one wide signal collapse onto its crest.
  found.sort((a, b) => b.snrDb - a.snrDb);
  const accepted: Array<{ bin: number; snrDb: number }> = [];
  for (const p of found) {
    if (accepted.length >= PEAK_MAX_COUNT) break;
    let clear = true;
    for (const q of accepted) {
      if (Math.abs(q.bin - p.bin) < PEAK_MIN_SPACING_BINS) {
        clear = false;
        break;
      }
    }
    if (clear) accepted.push(p);
  }
  return accepted.map((p) => ({ hz: centerHz + (p.bin - half) * hzPerPixel, snrDb: p.snrDb }));
}

/** Normalised marker opacity (0..1) for a peak SNR, for the amber tick alpha. */
export function peakAlpha(snrDb: number): number {
  return Math.max(0.45, Math.min(1, snrDb / PEAK_FULL_SNR_DB));
}

/** Measure a carrier's occupied bandwidth: walk outward from the crest bin until
 *  the level falls `dropDb` below the crest (the −6 dB / −26 dB edges), bounded
 *  to ±maxRadiusBins and stopping early at a rising edge (valley) so an adjacent
 *  signal is not swallowed. Returns inclusive [loBin, hiBin]. Pure — caller
 *  supplies the live spectrum. */
export function measureOccupiedBandwidth(
  spec: Float32Array,
  centerBin: number,
  dropDb: number,
  maxRadiusBins: number,
): [number, number] {
  const n = spec.length;
  if (n === 0) return [0, 0];
  const cb = Math.max(0, Math.min(n - 1, Math.round(centerBin)));
  const crest = spec[cb]!;
  const thresh = crest - dropDb;
  let lo = cb;
  let prev = crest;
  for (let i = cb - 1, stop = Math.max(0, cb - maxRadiusBins); i >= stop; i--) {
    const v = spec[i]!;
    if (v < thresh) break; // dropped past the edge
    if (v > prev + 3) break; // rising again → into an adjacent signal
    prev = v;
    lo = i;
  }
  prev = crest;
  let hi = cb;
  for (let i = cb + 1, stop = Math.min(n - 1, cb + maxRadiusBins); i <= stop; i++) {
    const v = spec[i]!;
    if (v < thresh) break;
    if (v > prev + 3) break;
    prev = v;
    hi = i;
  }
  return [lo, hi];
}

/** Measure a signal's VISIBLE extent: walk outward from the crest until the
 *  level sinks to within `edgeMarginDb` of the LOCAL noise floor (i.e. the
 *  signal has dropped into the noise) — the edge the eye reads as "where the
 *  signal ends". More faithful than a fixed crest-relative drop because it
 *  adapts to each signal's SNR. Stops early at a rising edge (adjacent signal)
 *  and is bounded to ±maxRadiusBins. Returns inclusive [loBin, hiBin]. Pure. */
export function measureSignalExtent(
  spec: Float32Array,
  floorArr: Float32Array | null,
  centerBin: number,
  edgeMarginDb: number,
  maxRadiusBins: number,
): [number, number] {
  const n = spec.length;
  if (n === 0) return [0, 0];
  const haveFloor = floorArr !== null && floorArr.length === n;
  const cb = Math.max(0, Math.min(n - 1, Math.round(centerBin)));
  const edgeOf = (i: number) => (haveFloor ? floorArr![i]! : -200) + edgeMarginDb;
  let lo = cb;
  let prev = spec[cb]!;
  for (let i = cb - 1, stop = Math.max(0, cb - maxRadiusBins); i >= stop; i--) {
    const v = spec[i]!;
    if (v <= edgeOf(i)) break; // sank into the noise
    if (v > prev + 3) break; // rising again → adjacent signal
    prev = v;
    lo = i;
  }
  prev = spec[cb]!;
  let hi = cb;
  for (let i = cb + 1, stop = Math.min(n - 1, cb + maxRadiusBins); i <= stop; i++) {
    const v = spec[i]!;
    if (v <= edgeOf(i)) break;
    if (v > prev + 3) break;
    prev = v;
    hi = i;
  }
  return [lo, hi];
}

/** Snap target for a click: the detected peak (same set the markers draw)
 *  nearest to `clickHz`, within `maxRadiusHz`. Returns its exact bin-centre
 *  frequency, or null if no carrier is close enough — so a click over bare
 *  spectrum tunes normally. The radius is supplied by the caller (derived from
 *  a screen-pixel distance) so snapping feels the same at every zoom. */
export function findNearestPeakHz(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  clickHz: number,
  maxRadiusHz: number,
): number | null {
  const peaks = detectPeaks(spec, centerHz, hzPerPixel);
  let best: number | null = null;
  let bestDist = Infinity;
  for (const p of peaks) {
    const d = Math.abs(p.hz - clickHz);
    if (d <= maxRadiusHz && d < bestDist) {
      bestDist = d;
      best = p.hz;
    }
  }
  return best;
}

// Parabolic interpolation around `bin` (dB domain) → sub-bin peak position, for
// carrier precision finer than one FFT bin. Returns a fractional bin index.
function refinePeakBin(spec: Float32Array, bin: number): number {
  const n = spec.length;
  if (bin <= 0 || bin >= n - 1) return bin;
  const l = spec[bin - 1]!;
  const c = spec[bin]!;
  const r = spec[bin + 1]!;
  const denom = l - 2 * c + r;
  if (denom === 0) return bin;
  let delta = (0.5 * (l - r)) / denom;
  if (delta < -1) delta = -1;
  else if (delta > 1) delta = 1;
  return bin + delta;
}

// Walk out from `startBin` in `dir` (±1) while the spectrum stays above the
// floor, tolerating a few sub-threshold bins, and return the last in-signal bin
// — the signal's energy edge on that side. Capped at `maxBins`.
function walkEdgeBin(spec: Float32Array, startBin: number, dir: number, maxBins: number): number {
  const n = spec.length;
  const f = floor;
  let edge = startBin;
  let gap = 0;
  let i = startBin;
  for (let steps = 0; steps < maxBins; steps++) {
    const j = i + dir;
    if (j < 0 || j >= n) break;
    const fl = f && f.length === n ? f[j]! : -200;
    if (spec[j]! > fl + coherentThreshold(SNAP_EDGE_SNR_DB, j, n)) {
      edge = j;
      gap = 0;
    } else if (++gap > SNAP_EDGE_GAP_BINS) {
      break;
    }
    i = j;
  }
  return edge;
}

/** Power-weighted centroid bin over [lo, hi] (carrier centre for AM/FM/DSB). */
function centroidBin(spec: Float32Array, lo: number, hi: number): number {
  const n = spec.length;
  const f = floor;
  let wsum = 0;
  let bsum = 0;
  for (let i = lo; i <= hi; i++) {
    const fl = f && f.length === n ? f[i]! : -200;
    const snr = spec[i]! - fl;
    if (snr <= 0) continue;
    const w = Math.pow(10, snr / 10); // dB → linear power
    wsum += w;
    bsum += w * i;
  }
  return wsum > 0 ? bsum / wsum : 0.5 * (lo + hi);
}

/** Mode-aware "tune in perfectly" target for a snap click. Anchors on the
 *  strongest bin near the click (not a pre-detected peak — so clicking anywhere
 *  ON a signal works, not just on its crest), walks out to that signal's full
 *  extent, then returns the DIAL frequency to POST so it demodulates correctly:
 *
 *    • CW (CWU/CWL) — dial = carrier crest (sub-bin). The backend offsets the
 *      LO by the sidetone pitch (CwOffset.EffectiveLoHz) → zero-beat.
 *    • USB / DIGU   — dial = LOW edge of the signal (suppressed carrier), so the
 *      whole voice channel sits inside the passband.
 *    • LSB / DIGL   — dial = HIGH edge.
 *    • AM/SAM/DSB/FM — dial = energy centroid (carrier centre).
 *
 *  Returns null only when the click is over bare noise (no bin within
 *  `maxRadiusHz` rises above the floor). */
export function computeSnapTuneHz(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  clickHz: number,
  maxRadiusHz: number,
  mode: RxMode,
): number | null {
  const n = spec.length;
  if (n < 3 || hzPerPixel <= 0) return null;
  const f = floor;
  const half = n / 2;
  const st = useSignalEnhanceStore.getState();
  const flAt = (i: number): number => (f && f.length === n ? f[i]! : -200);

  // Anchor: the strongest bin within the search radius that clears the floor.
  // This is what makes "click in the middle of a signal" work — we don't
  // require the click to land on a detected peak.
  const clickBin = Math.round((clickHz - centerHz) / hzPerPixel + half);
  const radius = Math.max(2, Math.round(maxRadiusHz / hzPerPixel));
  const lo = Math.max(1, clickBin - radius);
  const hi = Math.min(n - 2, clickBin + radius);
  let anchor = -1;
  let anchorVal = -Infinity;
  for (let i = lo; i <= hi; i++) {
    if (spec[i]! < flAt(i) + coherentThreshold(st.snapMinSnrDb, i, n)) continue;
    if (spec[i]! > anchorVal) {
      anchorVal = spec[i]!;
      anchor = i;
    }
  }
  if (anchor < 0) return null; // clicked on bare noise — tune normally.

  const binToHz = (b: number): number => centerHz + (b - half) * hzPerPixel;
  const maxBins = Math.max(2, Math.round(SNAP_MAX_SIGNAL_HZ / hzPerPixel));
  const loEdge = walkEdgeBin(spec, anchor, -1, maxBins);
  const hiEdge = walkEdgeBin(spec, anchor, +1, maxBins);

  switch (mode) {
    case 'CWU':
    case 'CWL': {
      // Narrow carrier: the crest of the blob, sub-bin. Backend adds the pitch.
      let pk = anchor;
      for (let i = loEdge; i <= hiEdge; i++) if (spec[i]! > spec[pk]!) pk = i;
      return binToHz(refinePeakBin(spec, pk));
    }
    case 'USB':
    case 'DIGU':
      return binToHz(loEdge);
    case 'LSB':
    case 'DIGL':
      return binToHz(hiEdge);
    default:
      // AM / SAM / DSB / FM: centre the dial on the carrier (energy centroid).
      return binToHz(centroidBin(spec, loEdge, hiEdge));
  }
}

/** The dial frequency that demodulates a signal cluster [loEdge, hiEdge]
 *  correctly for `mode` — the same placement rule computeSnapTuneHz uses, but
 *  factored out so the line-favouring snap can evaluate every candidate. */
function modeTuneHz(
  spec: Float32Array,
  loEdge: number,
  hiEdge: number,
  mode: RxMode,
  binToHz: (b: number) => number,
): number {
  switch (mode) {
    case 'CWU':
    case 'CWL': {
      let pk = loEdge;
      for (let i = loEdge; i <= hiEdge; i++) if (spec[i]! > spec[pk]!) pk = i;
      return binToHz(refinePeakBin(spec, pk));
    }
    case 'USB':
    case 'DIGU':
      return binToHz(loEdge);
    case 'LSB':
    case 'DIGL':
      return binToHz(hiEdge);
    default:
      return binToHz(centroidBin(spec, loEdge, hiEdge));
  }
}

/** Snap that FAVOURS THE LINE UNDER THE CURSOR. Scans the visible spectrum for
 *  signal clusters, picks the one nearest the clicked line `lineHz`, and returns
 *  that signal's mode-aware tuning edge — the dial freq that demodulates it
 *  correctly (USB low edge, LSB high edge, CW zero-beat, AM centroid). So a
 *  click locks onto the signal you clicked, edge-aligned for the mode, instead
 *  of the exact sub-Hz pixel under the cursor — and it tracks WHERE you clicked
 *  rather than collapsing to the display centre.
 *
 *  Selection is by distance to the signal's BODY [loEdge, hiEdge] (zero when the
 *  click lands inside the signal), NOT to its tuning edge. That matters for wide
 *  SSB: clicking the low side of a 3 kHz LSB channel must still grab it even
 *  though its tuning (high) edge is kilohertz away — the dial then lands on that
 *  high edge. Clusters whose body is more than `maxRadiusHz` from the click are
 *  ignored, so a click over bare spectrum returns null and the caller tunes
 *  normally at the click point. */
export function computeSnapToLineHz(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  mode: RxMode,
  lineHz: number,
  maxRadiusHz: number,
): number | null {
  const n = spec.length;
  if (n < 3 || hzPerPixel <= 0) return null;
  const f = floor;
  const half = n / 2;
  const st = useSignalEnhanceStore.getState();
  const flAt = (i: number): number => (f && f.length === n ? f[i]! : -200);
  const binToHz = (b: number): number => centerHz + (b - half) * hzPerPixel;
  const maxBins = Math.max(2, Math.round(SNAP_MAX_SIGNAL_HZ / hzPerPixel));

  let best: number | null = null;
  let bestDist = Infinity;
  let i = 1;
  const end = n - 2;
  while (i <= end) {
    if (spec[i]! < flAt(i) + coherentThreshold(st.snapMinSnrDb, i, n)) {
      i++;
      continue;
    }
    // Found a cluster — measure its full energy extent, then score it by how
    // close the click sits to the signal BODY (0 when the click is inside it),
    // so clicking any part of a wide signal grabs it; the dial still lands on
    // the mode-tuning edge.
    const loEdge = walkEdgeBin(spec, i, -1, maxBins);
    const hiEdge = walkEdgeBin(spec, i, +1, maxBins);
    const loHz = binToHz(loEdge);
    const hiHz = binToHz(hiEdge);
    const dist = lineHz < loHz ? loHz - lineHz : lineHz > hiHz ? lineHz - hiHz : 0;
    if (dist <= maxRadiusHz && dist < bestDist) {
      bestDist = dist;
      best = modeTuneHz(spec, loEdge, hiEdge, mode, binToHz);
    }
    i = Math.max(i + 1, hiEdge + 1);
  }
  return best;
}

/** The energy extent of the signal cluster nearest `nearHz`, using the SAME
 *  confidence-aware, gap-tolerant edge walk the snap tuner uses (walkEdgeBin) —
 *  so a panel that DRAWS these edges shows exactly where a snap click would tune.
 *  Anchors on the strongest bin within `maxRadiusHz` of nearHz (so it locks onto
 *  the carrier even if nearHz is a few Hz off), walks both energy edges, and
 *  returns absolute { loHz, hiHz, crestHz } with a sub-bin crest. Null over bare
 *  noise (nothing within radius clears the floor). Pure: caller supplies the live
 *  spectrum + geometry; the shared floor must already be populated. */
export function signalExtentHz(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  nearHz: number,
  maxRadiusHz: number,
): { loHz: number; hiHz: number; crestHz: number } | null {
  const n = spec.length;
  if (n < 3 || hzPerPixel <= 0) return null;
  const f = floor;
  const half = n / 2;
  const st = useSignalEnhanceStore.getState();
  const flAt = (i: number): number => (f && f.length === n ? f[i]! : -200);
  const binToHz = (b: number): number => centerHz + (b - half) * hzPerPixel;
  const clickBin = Math.round((nearHz - centerHz) / hzPerPixel + half);
  const radius = Math.max(2, Math.round(maxRadiusHz / hzPerPixel));
  const lo = Math.max(1, clickBin - radius);
  const hi = Math.min(n - 2, clickBin + radius);
  let anchor = -1;
  let anchorVal = -Infinity;
  for (let i = lo; i <= hi; i++) {
    if (spec[i]! < flAt(i) + coherentThreshold(st.snapMinSnrDb, i, n)) continue;
    if (spec[i]! > anchorVal) {
      anchorVal = spec[i]!;
      anchor = i;
    }
  }
  if (anchor < 0) return null;
  const maxBins = Math.max(2, Math.round(SNAP_MAX_SIGNAL_HZ / hzPerPixel));
  const loEdge = walkEdgeBin(spec, anchor, -1, maxBins);
  const hiEdge = walkEdgeBin(spec, anchor, +1, maxBins);
  let pk = anchor;
  for (let i = loEdge; i <= hiEdge; i++) if (spec[i]! > spec[pk]!) pk = i;
  return { loHz: binToHz(loEdge), hiHz: binToHz(hiEdge), crestHz: binToHz(refinePeakBin(spec, pk)) };
}

export type SnapLockMeasure = {
  /** Mode-tuning dial for the locked signal this frame (USB low edge, etc.). */
  dialHz: number;
  /** Energy-centroid of the locked signal — the anchor to track frame to frame. */
  bodyHz: number;
};

/** Re-measure a LOCKED signal for the self-correcting snap tracker. Searches a
 *  DELIBERATELY NARROW window (±captureHz) around `anchorBodyHz` for the local
 *  peak NEAREST the anchor — not the strongest in view — then walks that signal's
 *  own energy extent and returns its mode-tuning dial plus its centroid body.
 *
 *  This is the whole answer to "don't get confused by a loud neighbour while
 *  pulling in a weak one": the search never looks past ±captureHz, and the
 *  caller keeps captureHz far smaller than the spacing to the neighbour and
 *  clamps how far the anchor may ever roam — so a stronger signal outside the
 *  window can NEVER steal the lock. Returns null when the locked signal has sunk
 *  into the noise inside the window, so the caller HOLDS rather than jumping to
 *  whatever else is on the band. Pure: caller supplies the live spectrum; the
 *  shared floor must already be populated. */
export function measureSnapLock(
  spec: Float32Array,
  centerHz: number,
  hzPerPixel: number,
  mode: RxMode,
  anchorBodyHz: number,
  captureHz: number,
): SnapLockMeasure | null {
  const n = spec.length;
  if (n < 3 || hzPerPixel <= 0) return null;
  const f = floor;
  const half = n / 2;
  const st = useSignalEnhanceStore.getState();
  const flAt = (i: number): number => (f && f.length === n ? f[i]! : -200);
  const binToHz = (b: number): number => centerHz + (b - half) * hzPerPixel;
  const anchorBin = Math.round((anchorBodyHz - centerHz) / hzPerPixel + half);
  const capBins = Math.max(1, Math.round(captureHz / hzPerPixel));
  const lo = Math.max(1, anchorBin - capBins);
  const hi = Math.min(n - 2, anchorBin + capBins);

  // The peak NEAREST the anchor that clears the floor — continuity keeps us on
  // OUR signal frame to frame instead of hopping to a louder bin in the window.
  let bestBin = -1;
  let bestDist = Infinity;
  for (let i = lo; i <= hi; i++) {
    if (spec[i]! < flAt(i) + coherentThreshold(st.snapMinSnrDb, i, n)) continue;
    if (spec[i]! >= spec[i - 1]! && spec[i]! >= spec[i + 1]!) {
      const d = Math.abs(i - anchorBin);
      if (d < bestDist) {
        bestDist = d;
        bestBin = i;
      }
    }
  }
  if (bestBin < 0) return null; // locked signal is in the noise this frame — hold.

  const maxBins = Math.max(2, Math.round(SNAP_MAX_SIGNAL_HZ / hzPerPixel));
  const loEdge = walkEdgeBin(spec, bestBin, -1, maxBins);
  const hiEdge = walkEdgeBin(spec, bestBin, +1, maxBins);
  return {
    dialHz: modeTuneHz(spec, loEdge, hiEdge, mode, binToHz),
    bodyHz: binToHz(centroidBin(spec, loEdge, hiEdge)),
  };
}
