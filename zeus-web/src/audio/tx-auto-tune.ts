// SPDX-License-Identifier: GPL-2.0-or-later

import type { CfcConfigDto, TxLevelingConfigDto } from '../api/client';

const DBFS_FLOOR = -200;
const MIN_VOICED_SAMPLES = 8;

// CFC pre-comp / pre-PEQ are unbounded on the server; the frontend owns the
// ±12 dB operational window (matches the CFC panel + withPreComp).
const CFC_SHAPE_MIN = -12;
const CFC_SHAPE_MAX = 12;

// TxLeveling clamps. The hard server bounds (RadioService.SetTxLeveling) are
// ALC max [0,120], ALC decay [1,50], leveler decay [1,5000], comp gain [0,20].
// Auto Tune uses TIGHTER operational ceilings when it RAISES a value so it
// never drives the chain to an extreme in a single bounded run, but it clamps
// the carried-forward config only to the hard bounds — so a no-op run never
// rewrites an operator-set value that already sits above the operational cap.
const ALC_MAX_HARD_MIN = 0;
const ALC_MAX_HARD_MAX = 120;
const ALC_MAX_RAISE_GUARD = 6; // only auto-raise ALC make-up below this
const ALC_MAX_RAISE_CEIL = 8; // and never above this via a raise step
const ALC_DECAY_MIN = 1;
const ALC_DECAY_MAX = 50;
const LEVELER_DECAY_MIN = 1;
const LEVELER_DECAY_MAX = 5000;
const LEVELER_DECAY_DENSITY_CEIL = 180; // smooth-for-density target
const LEVELER_DECAY_PUMP_CEIL = 400; // slow the leveler to stop pumping
const COMP_GAIN_HARD_MIN = 0;
const COMP_GAIN_HARD_MAX = 20;
const COMP_GAIN_RAISE_CEIL = 10; // operational density ceiling

export type TxAutoTuneSample = {
  micPkDbfs: number | null;
  outPkDbfs: number | null;
  outAvDbfs: number | null;
  audioSuiteOutputDbfs: number | null;
  compPkDbfs: number | null;
  compAvDbfs: number | null;
  alcGrDb: number;
  lvlrGrDb: number;
  cfcGrDb: number;
  swr: number;
  psFeedbackLevel: number | null;
  vstDegradedDelta: number | null;
  ingestDroppedFrameDelta: number | null;
  txBlockDelta: number | null;
  p2QueuedPackets: number | null;
  p2TransportFailureDelta: number | null;
  p2QueueFailureDelta: number | null;
};

export type TxAutoTuneSettings = {
  micGainDb: number;
  levelerMaxGainDb: number;
  drivePercent: number;
  cfcConfig: CfcConfigDto;
  txLeveling: TxLevelingConfigDto;
  targetSpectralDensity: number;
  keyed: boolean;
  audioSuiteActive: boolean;
  vstActive: boolean;
};

export type TxAutoTuneStats = {
  sampleCount: number;
  voicedSampleCount: number;
  micP95Dbfs: number | null;
  micMaxDbfs: number | null;
  outP95Dbfs: number | null;
  outMaxDbfs: number | null;
  audioSuiteOutP95Dbfs: number | null;
  audioSuiteOutMaxDbfs: number | null;
  crestMedianDb: number | null;
  compCrestMedianDb: number | null;
  alcP95Db: number;
  lvlrP95Db: number;
  lvlrGrSpreadDb: number;
  cfcP95Db: number;
  swrP95: number;
  psFeedbackMedian: number | null;
  vstDegradedBlocks: number;
  ingestDroppedFrames: number;
  txBlocksProcessed: number;
  p2QueuedPacketsMax: number;
  p2TransportFailures: number;
  p2QueueFailures: number;
};

export type TxAutoTunePlan = {
  settings: TxAutoTuneSettings;
  stats: TxAutoTuneStats;
  actions: string[];
  blockers: string[];
  changed: boolean;
  summary: string;
};

function finite(v: number | null | undefined): v is number {
  return typeof v === 'number' && Number.isFinite(v);
}

function validDbfs(v: number | null | undefined): v is number {
  return finite(v) && v > DBFS_FLOOR;
}

function nonNegative(v: number | null | undefined): number {
  return finite(v) && v > 0 ? v : 0;
}

function clamp(v: number, min: number, max: number): number {
  if (!Number.isFinite(v)) return min;
  return Math.max(min, Math.min(max, v));
}

function roundHalf(v: number): number {
  return Math.round(v * 2) / 2;
}

function roundTenth(v: number): number {
  return Math.round(v * 10) / 10;
}

function roundInt(v: number): number {
  return Math.round(v);
}

function values(samples: ReadonlyArray<TxAutoTuneSample>, pick: (s: TxAutoTuneSample) => number | null): number[] {
  return samples.map(pick).filter(validDbfs);
}

function grValues(samples: ReadonlyArray<TxAutoTuneSample>, pick: (s: TxAutoTuneSample) => number): number[] {
  return samples.map((s) => nonNegative(pick(s)));
}

function percentile(items: number[], p: number): number | null {
  if (items.length === 0) return null;
  const sorted = items.slice().sort((a, b) => a - b);
  const idx = clamp((sorted.length - 1) * p, 0, sorted.length - 1);
  const lo = Math.floor(idx);
  const hi = Math.ceil(idx);
  if (lo === hi) return sorted[lo]!;
  const f = idx - lo;
  return sorted[lo]! * (1 - f) + sorted[hi]! * f;
}

function maxOrNull(items: number[]): number | null {
  return items.length === 0 ? null : Math.max(...items);
}

function crestValues(samples: ReadonlyArray<TxAutoTuneSample>): number[] {
  const out: number[] = [];
  for (const s of samples) {
    if (!validDbfs(s.outPkDbfs) || !validDbfs(s.outAvDbfs) || s.outAvDbfs > s.outPkDbfs) continue;
    out.push(s.outPkDbfs - s.outAvDbfs);
  }
  return out;
}

function compCrestValues(samples: ReadonlyArray<TxAutoTuneSample>): number[] {
  const out: number[] = [];
  for (const s of samples) {
    if (!validDbfs(s.compPkDbfs) || !validDbfs(s.compAvDbfs) || s.compAvDbfs > s.compPkDbfs) continue;
    out.push(s.compPkDbfs - s.compAvDbfs);
  }
  return out;
}

function sumDeltas(samples: ReadonlyArray<TxAutoTuneSample>, pick: (s: TxAutoTuneSample) => number | null): number {
  let total = 0;
  for (const sample of samples) {
    const v = pick(sample);
    if (finite(v) && v > 0) total += v;
  }
  return total;
}

function maxCount(samples: ReadonlyArray<TxAutoTuneSample>, pick: (s: TxAutoTuneSample) => number | null): number {
  let max = 0;
  for (const sample of samples) {
    const v = pick(sample);
    if (finite(v) && v > max) max = v;
  }
  return max;
}

function cloneCfc(config: CfcConfigDto): CfcConfigDto {
  return {
    ...config,
    bands: config.bands.map((band) => ({ ...band })),
  };
}

function withPreComp(config: CfcConfigDto, deltaDb: number, forceEnable: boolean): CfcConfigDto {
  const next = cloneCfc(config);
  if (forceEnable) {
    next.enabled = true;
    next.postEqEnabled = true;
  }
  next.preCompDb = roundTenth(clamp(next.preCompDb + deltaDb, CFC_SHAPE_MIN, CFC_SHAPE_MAX));
  return next;
}

function withPrePeq(config: CfcConfigDto, deltaDb: number, forceEnable: boolean): CfcConfigDto {
  const next = cloneCfc(config);
  if (forceEnable) next.enabled = true;
  next.prePeqDb = roundTenth(clamp(next.prePeqDb + deltaDb, CFC_SHAPE_MIN, CFC_SHAPE_MAX));
  return next;
}

// Carry the operator's leveling config forward, clamped only to the HARD server
// bounds so a run that touches nothing rewrites no value. Tuning rules below
// apply their own tighter operational ceilings when they actively raise a value.
function clampLeveling(c: TxLevelingConfigDto): TxLevelingConfigDto {
  return {
    alcMaxGainDb: clamp(c.alcMaxGainDb, ALC_MAX_HARD_MIN, ALC_MAX_HARD_MAX),
    alcDecayMs: roundInt(clamp(c.alcDecayMs, ALC_DECAY_MIN, ALC_DECAY_MAX)),
    levelerEnabled: c.levelerEnabled,
    levelerDecayMs: roundInt(clamp(c.levelerDecayMs, LEVELER_DECAY_MIN, LEVELER_DECAY_MAX)),
    compressorEnabled: c.compressorEnabled,
    compressorGainDb: roundTenth(clamp(c.compressorGainDb, COMP_GAIN_HARD_MIN, COMP_GAIN_HARD_MAX)),
  };
}

export function levelingChanged(a: TxLevelingConfigDto, b: TxLevelingConfigDto): boolean {
  return (
    a.alcMaxGainDb !== b.alcMaxGainDb ||
    a.alcDecayMs !== b.alcDecayMs ||
    a.levelerEnabled !== b.levelerEnabled ||
    a.levelerDecayMs !== b.levelerDecayMs ||
    a.compressorEnabled !== b.compressorEnabled ||
    a.compressorGainDb !== b.compressorGainDb
  );
}

function settingChanged(a: TxAutoTuneSettings, b: TxAutoTuneSettings): boolean {
  return (
    a.micGainDb !== b.micGainDb ||
    a.levelerMaxGainDb !== b.levelerMaxGainDb ||
    a.drivePercent !== b.drivePercent ||
    a.cfcConfig.enabled !== b.cfcConfig.enabled ||
    a.cfcConfig.postEqEnabled !== b.cfcConfig.postEqEnabled ||
    a.cfcConfig.preCompDb !== b.cfcConfig.preCompDb ||
    a.cfcConfig.prePeqDb !== b.cfcConfig.prePeqDb ||
    a.cfcConfig.bands.some((band, i) => {
      const other = b.cfcConfig.bands[i];
      return !other ||
        band.freqHz !== other.freqHz ||
        band.compLevelDb !== other.compLevelDb ||
        band.postGainDb !== other.postGainDb;
    }) ||
    levelingChanged(a.txLeveling, b.txLeveling)
  );
}

function fmtDb(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dB`;
}

function fmtDbfs(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dBFS`;
}

export function summarizeTxAutoTuneSamples(samples: ReadonlyArray<TxAutoTuneSample>): TxAutoTuneStats {
  const mic = values(samples, (s) => s.micPkDbfs);
  const out = values(samples, (s) => s.outPkDbfs);
  const suiteOut = values(samples, (s) => s.audioSuiteOutputDbfs);
  const crest = crestValues(samples);
  const compCrest = compCrestValues(samples);
  const alc = grValues(samples, (s) => s.alcGrDb);
  const lvlr = grValues(samples, (s) => s.lvlrGrDb);
  const cfc = grValues(samples, (s) => s.cfcGrDb);
  const lvlrP95 = percentile(lvlr, 0.95) ?? 0;
  const lvlrP50 = percentile(lvlr, 0.5) ?? 0;
  const swr = samples
    .map((s) => s.swr)
    .filter((v) => Number.isFinite(v) && v > 0);
  const psFeedback = samples
    .map((s) => s.psFeedbackLevel)
    .filter((v): v is number => finite(v) && v > 0);
  const voicedSampleCount = samples.filter(
    (s) =>
      (validDbfs(s.micPkDbfs) && s.micPkDbfs > -55) ||
      (validDbfs(s.outPkDbfs) && s.outPkDbfs > -55),
  ).length;

  return {
    sampleCount: samples.length,
    voicedSampleCount,
    micP95Dbfs: percentile(mic, 0.95),
    micMaxDbfs: maxOrNull(mic),
    outP95Dbfs: percentile(out, 0.95),
    outMaxDbfs: maxOrNull(out),
    audioSuiteOutP95Dbfs: percentile(suiteOut, 0.95),
    audioSuiteOutMaxDbfs: maxOrNull(suiteOut),
    crestMedianDb: percentile(crest, 0.5),
    compCrestMedianDb: percentile(compCrest, 0.5),
    alcP95Db: percentile(alc, 0.95) ?? 0,
    lvlrP95Db: lvlrP95,
    lvlrGrSpreadDb: Math.max(0, lvlrP95 - lvlrP50),
    cfcP95Db: percentile(cfc, 0.95) ?? 0,
    swrP95: percentile(swr, 0.95) ?? 1,
    psFeedbackMedian: percentile(psFeedback, 0.5),
    vstDegradedBlocks: sumDeltas(samples, (s) => s.vstDegradedDelta),
    ingestDroppedFrames: sumDeltas(samples, (s) => s.ingestDroppedFrameDelta),
    txBlocksProcessed: sumDeltas(samples, (s) => s.txBlockDelta),
    p2QueuedPacketsMax: maxCount(samples, (s) => s.p2QueuedPackets),
    p2TransportFailures: sumDeltas(samples, (s) => s.p2TransportFailureDelta),
    p2QueueFailures: sumDeltas(samples, (s) => s.p2QueueFailureDelta),
  };
}

export function recommendTxAutoTune(
  current: TxAutoTuneSettings,
  samples: ReadonlyArray<TxAutoTuneSample>,
): TxAutoTunePlan {
  const stats = summarizeTxAutoTuneSamples(samples);
  const next: TxAutoTuneSettings = {
    ...current,
    micGainDb: roundInt(clamp(current.micGainDb, -40, 10)),
    levelerMaxGainDb: roundHalf(clamp(current.levelerMaxGainDb, 0, 20)),
    drivePercent: roundInt(clamp(current.drivePercent, 0, 100)),
    cfcConfig: cloneCfc(current.cfcConfig),
    txLeveling: clampLeveling(current.txLeveling),
  };
  const actions: string[] = [];
  const blockers: string[] = [];

  if (stats.voicedSampleCount < MIN_VOICED_SAMPLES) {
    blockers.push('not enough voiced samples');
  }
  if (stats.txBlocksProcessed <= 0) {
    blockers.push('TXA did not process fresh mic blocks during sampling');
    return {
      settings: current,
      stats,
      actions,
      blockers,
      changed: false,
      summary: 'Auto tune needs fresh TX monitor samples',
    };
  }
  if (stats.swrP95 >= 2.5) {
    blockers.push('SWR is too high for automatic power optimization');
  }
  const psFeedback = stats.psFeedbackMedian;
  if (psFeedback !== null && (psFeedback < 96 || psFeedback > 210)) {
    blockers.push('PureSignal feedback is outside the safe tuning range');
  }

  const engineBlocked =
    stats.vstDegradedBlocks > 0 ||
    stats.ingestDroppedFrames > 0 ||
    stats.p2QueuedPacketsMax > 0 ||
    stats.p2TransportFailures > 0 ||
    stats.p2QueueFailures > 0;
  if (engineBlocked) {
    blockers.push('audio engine or TX transport counters moved during sampling');
  }

  const suiteHot =
    current.audioSuiteActive &&
    ((stats.audioSuiteOutMaxDbfs !== null && stats.audioSuiteOutMaxDbfs > -0.5) ||
      (stats.audioSuiteOutP95Dbfs !== null && stats.audioSuiteOutP95Dbfs > -3));
  const outputHot =
    (stats.outMaxDbfs !== null && stats.outMaxDbfs > -0.5) ||
    (stats.outP95Dbfs !== null && stats.outP95Dbfs > -3);
  const micHot =
    (stats.micMaxDbfs !== null && stats.micMaxDbfs > -1) ||
    (stats.micP95Dbfs !== null && stats.micP95Dbfs > -6);
  const dynamicsHot =
    stats.alcP95Db > 8 ||
    stats.lvlrP95Db > 8 ||
    stats.cfcP95Db > 6 ||
    (stats.crestMedianDb !== null && stats.crestMedianDb < 6);

  if (suiteHot || micHot) {
    const hard = (stats.audioSuiteOutMaxDbfs ?? -99) > -0.5 || (stats.micMaxDbfs ?? -99) > -1;
    const deltaDb = hard ? -3 : -1.5;
    next.micGainDb = roundInt(clamp(next.micGainDb + deltaDb, -40, 10));
    actions.push(`mic ${deltaDb.toFixed(1)} dB for headroom`);
  }
  if (outputHot) {
    if (current.keyed && next.drivePercent > 5) {
      next.drivePercent = roundInt(clamp(next.drivePercent - 5, 0, 100));
      actions.push('drive -5% for TX output headroom');
    } else if (!suiteHot && !micHot) {
      next.micGainDb = roundInt(clamp(next.micGainDb - 1, -40, 10));
      actions.push('mic -1 dB for TX output headroom');
    }
  }
  if (stats.alcP95Db > 11) {
    next.micGainDb = roundInt(clamp(next.micGainDb - 2, -40, 10));
    actions.push('mic -2 dB to stop hard ALC limiting');
  } else if (stats.alcP95Db > 8) {
    next.micGainDb = roundInt(clamp(next.micGainDb - 1, -40, 10));
    actions.push('mic -1 dB to ease ALC');
  }
  if (stats.lvlrP95Db > 10) {
    next.levelerMaxGainDb = roundHalf(clamp(next.levelerMaxGainDb - 1.5, 0, 20));
    actions.push('leveler max -1.5 dB');
  } else if (stats.lvlrP95Db > 8) {
    next.levelerMaxGainDb = roundHalf(clamp(next.levelerMaxGainDb - 1, 0, 20));
    actions.push('leveler max -1 dB');
  }
  if (stats.cfcP95Db > 7 || (stats.crestMedianDb !== null && stats.crestMedianDb < 6)) {
    next.cfcConfig = withPreComp(next.cfcConfig, -0.8, false);
    actions.push('CFC pre-comp -0.8 dB');
  } else if (stats.cfcP95Db > 5) {
    next.cfcConfig = withPreComp(next.cfcConfig, -0.4, false);
    actions.push('CFC pre-comp -0.4 dB');
  }

  // ---- Protective leveling/compressor easing -------------------------------
  // When the ALC safety limiter is slamming, its make-up gain is fighting the
  // limiter — ease it so the upstream mic cut actually buys headroom instead of
  // being clawed back. Bounded -1 dB per run, floored at 0.
  if (stats.alcP95Db > 11 && next.txLeveling.alcMaxGainDb > ALC_MAX_HARD_MIN) {
    next.txLeveling = {
      ...next.txLeveling,
      alcMaxGainDb: roundTenth(clamp(next.txLeveling.alcMaxGainDb - 1, ALC_MAX_HARD_MIN, ALC_MAX_HARD_MAX)),
    };
    actions.push('ALC max gain -1 dB');
  }

  // CPDR compressor over-squash: only back the compander off when the
  // COMPRESSOR's OWN crest is pinched. A tight chain crest is just as often the
  // leveler or CFC doing the squashing, so attributing it to the compressor
  // would wrongly disable an operator's deliberate compression. When the comp
  // stage isn't metered (compCrestMedianDb null) we hold rather than guess.
  const compPinched =
    next.txLeveling.compressorEnabled &&
    next.txLeveling.compressorGainDb > COMP_GAIN_HARD_MIN &&
    stats.compCrestMedianDb !== null &&
    stats.compCrestMedianDb < 5;
  if (compPinched) {
    const reduced = roundTenth(clamp(next.txLeveling.compressorGainDb - 2, COMP_GAIN_HARD_MIN, COMP_GAIN_HARD_MAX));
    next.txLeveling = {
      ...next.txLeveling,
      compressorGainDb: reduced,
      compressorEnabled: reduced > 0,
    };
    actions.push(reduced > 0 ? 'compressor -2 dB' : 'compressor off');
  }

  // Leveler pumping: heavy leveler GR that swings sample-to-sample with a fast
  // decay is audible breathing. Slow the leveler so it rides the average
  // instead of chasing every syllable. Bounded toward the pump ceiling.
  if (
    stats.lvlrP95Db > 8 &&
    stats.lvlrGrSpreadDb > 6 &&
    next.txLeveling.levelerDecayMs < LEVELER_DECAY_PUMP_CEIL
  ) {
    const slower = roundInt(
      clamp(next.txLeveling.levelerDecayMs * 1.4 + 20, LEVELER_DECAY_MIN, LEVELER_DECAY_PUMP_CEIL),
    );
    if (slower > next.txLeveling.levelerDecayMs) {
      next.txLeveling = { ...next.txLeveling, levelerDecayMs: slower };
      actions.push('leveler decay slower');
    }
  }

  const protecting = suiteHot || outputHot || micHot || dynamicsHot;
  const mayRaise = blockers.length === 0 && !protecting;
  const target = clamp(current.targetSpectralDensity, 0, 100);
  if (mayRaise) {
    if (stats.micP95Dbfs !== null && stats.micP95Dbfs < -18) {
      const gain = clamp((-12 - stats.micP95Dbfs) * 0.4, 1, 3);
      next.micGainDb = roundInt(clamp(next.micGainDb + gain, -40, 10));
      actions.push(`mic +${gain.toFixed(1)} dB toward speech peak target`);
    } else if (
      stats.outP95Dbfs !== null &&
      stats.outP95Dbfs < -10 &&
      (stats.audioSuiteOutP95Dbfs === null || stats.audioSuiteOutP95Dbfs < -5) &&
      stats.alcP95Db < 5 &&
      stats.micP95Dbfs !== null &&
      stats.micP95Dbfs < -7
    ) {
      next.micGainDb = roundInt(clamp(next.micGainDb + 1, -40, 10));
      actions.push('mic +1 dB for clean output density');
    }

    if (target >= 70 && stats.lvlrP95Db < 2) {
      next.levelerMaxGainDb = roundHalf(clamp(next.levelerMaxGainDb + 1, 0, 20));
      actions.push('leveler max +1 dB');
    }
    if (
      target >= 80 &&
      stats.cfcP95Db < 3 &&
      stats.crestMedianDb !== null &&
      stats.crestMedianDb > (target >= 95 ? 8 : 10)
    ) {
      next.cfcConfig = withPreComp(next.cfcConfig, target >= 95 ? 0.8 : 0.5, true);
      actions.push(`CFC pre-comp +${target >= 95 ? '0.8' : '0.5'} dB`);
    }
    if (
      target >= 95 &&
      current.keyed &&
      stats.swrP95 < 1.8 &&
      psFeedback !== null &&
      psFeedback >= 128 &&
      psFeedback <= 181 &&
      stats.outP95Dbfs !== null &&
      stats.outP95Dbfs <= -4 &&
      next.drivePercent < 100
    ) {
      next.drivePercent = roundInt(clamp(next.drivePercent + 5, 0, 100));
      actions.push('drive +5% after clean keyed sample');
    }

    // ---- Density-raising leveling/compressor (clean chain only) -----------
    // mayRaise already guarantees the chain is not protecting (ALC/leveler/CFC
    // GR moderate, crest >= 6, nothing hot), so these only add controlled
    // density when there is genuine headroom.

    // Smooth, dense leveling for higher targets: a very fast leveler that is
    // barely working leaves the audio peaky. Lengthen its decay toward the
    // density ceiling so quiet passages stay up without pumping.
    if (
      target >= 70 &&
      stats.lvlrP95Db < 4 &&
      stats.lvlrGrSpreadDb < 4 &&
      next.txLeveling.levelerDecayMs < LEVELER_DECAY_DENSITY_CEIL
    ) {
      const slower = roundInt(
        clamp(next.txLeveling.levelerDecayMs + 40, LEVELER_DECAY_MIN, LEVELER_DECAY_DENSITY_CEIL),
      );
      if (slower > next.txLeveling.levelerDecayMs) {
        next.txLeveling = { ...next.txLeveling, levelerDecayMs: slower };
        actions.push('leveler decay +40 ms for density');
      }
    }

    // ALC make-up headroom: when the ALC is barely engaging on a clean chain,
    // a little more make-up gain lifts the average without touching the
    // limiter. Bounded +1 dB, only below the raise guard/ceiling.
    if (
      target >= 80 &&
      stats.alcP95Db < 5 &&
      next.txLeveling.alcMaxGainDb < ALC_MAX_RAISE_GUARD
    ) {
      next.txLeveling = {
        ...next.txLeveling,
        alcMaxGainDb: roundTenth(
          clamp(next.txLeveling.alcMaxGainDb + 1, ALC_MAX_HARD_MIN, ALC_MAX_RAISE_CEIL),
        ),
      };
      actions.push('ALC max gain +1 dB');
    }

    // Presence pre-PEQ for high targets: a small CFC pre-PEQ lift adds speech
    // articulation density once compression is already controlled.
    if (
      target >= 85 &&
      stats.cfcP95Db < 4 &&
      stats.crestMedianDb !== null &&
      stats.crestMedianDb >= 7 &&
      next.cfcConfig.prePeqDb < 4
    ) {
      next.cfcConfig = withPrePeq(next.cfcConfig, 0.4, true);
      actions.push('CFC pre-PEQ +0.4 dB');
    }

    // CPDR compressor for max density: the compander is the strongest clean
    // talk-power lever. Only engage it on a wide-open crest with real output
    // headroom so it tightens dynamics instead of pinching them. Unlike the
    // drive raise (keyed-only, because it touches RF) this is deliberately
    // allowed in silent Preview — the compressor is a pre-RF audio stage, so
    // shaping it from a Preview sample is safe and matches how the operator
    // would dial it in off-air.
    if (
      target >= 90 &&
      stats.alcP95Db < 6 &&
      stats.crestMedianDb !== null &&
      stats.crestMedianDb > 10 &&
      stats.outP95Dbfs !== null &&
      stats.outP95Dbfs <= -5 &&
      next.txLeveling.compressorGainDb < COMP_GAIN_RAISE_CEIL
    ) {
      const enabling = !next.txLeveling.compressorEnabled || next.txLeveling.compressorGainDb <= 0;
      const step = enabling ? 2 : 1;
      next.txLeveling = {
        ...next.txLeveling,
        compressorEnabled: true,
        compressorGainDb: roundTenth(
          clamp(next.txLeveling.compressorGainDb + step, COMP_GAIN_HARD_MIN, COMP_GAIN_RAISE_CEIL),
        ),
      };
      actions.push(enabling ? `compressor on +${step} dB` : `compressor +${step} dB`);
    }
  }

  next.micGainDb = roundInt(clamp(next.micGainDb, -40, 10));
  next.levelerMaxGainDb = roundHalf(clamp(next.levelerMaxGainDb, 0, 20));
  next.drivePercent = roundInt(clamp(next.drivePercent, 0, 100));
  next.txLeveling = clampLeveling(next.txLeveling);
  const changed = settingChanged(current, next);
  let summary = 'Auto tune held current settings';
  if (stats.voicedSampleCount < MIN_VOICED_SAMPLES) {
    summary = 'Auto tune needs a stronger speech sample';
  } else if (actions.length > 0) {
    summary = `Auto tune applied ${actions.length} bounded change${actions.length === 1 ? '' : 's'}`;
  } else if (blockers.length > 0) {
    summary = `Auto tune held settings: ${blockers[0]}`;
  } else {
    summary = `Auto tune targets are centered: mic ${fmtDbfs(stats.micP95Dbfs)}, OUT ${fmtDbfs(stats.outP95Dbfs)}, crest ${fmtDb(stats.crestMedianDb)}`;
  }

  return {
    settings: next,
    stats,
    actions,
    blockers,
    changed,
    summary,
  };
}
