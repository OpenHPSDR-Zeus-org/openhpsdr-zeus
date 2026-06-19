// SPDX-License-Identifier: GPL-2.0-or-later

import type { CfcConfigDto } from '../api/client';

const DBFS_FLOOR = -200;
const MIN_VOICED_SAMPLES = 8;

export type TxAutoTuneSample = {
  micPkDbfs: number | null;
  outPkDbfs: number | null;
  outAvDbfs: number | null;
  audioSuiteOutputDbfs: number | null;
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
  alcP95Db: number;
  lvlrP95Db: number;
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
  next.preCompDb = roundTenth(clamp(next.preCompDb + deltaDb, -12, 12));
  return next;
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
    })
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
  const alc = grValues(samples, (s) => s.alcGrDb);
  const lvlr = grValues(samples, (s) => s.lvlrGrDb);
  const cfc = grValues(samples, (s) => s.cfcGrDb);
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
    alcP95Db: percentile(alc, 0.95) ?? 0,
    lvlrP95Db: percentile(lvlr, 0.95) ?? 0,
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
  }

  next.micGainDb = roundInt(clamp(next.micGainDb, -40, 10));
  next.levelerMaxGainDb = roundHalf(clamp(next.levelerMaxGainDb, 0, 20));
  next.drivePercent = roundInt(clamp(next.drivePercent, 0, 100));
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
