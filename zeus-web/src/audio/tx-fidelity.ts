// SPDX-License-Identifier: GPL-2.0-or-later

const BYPASSED_DBFS_THRESHOLD = -200;
const DEFAULT_SPECTRAL_DENSITY_TARGET = 55;
const CLEAN_SPECTRAL_DENSITY_CEILING = 84;

export type TxFidelitySnapshot = {
  moxOn: boolean;
  tunOn: boolean;
  txMonitorEnabled: boolean;
  micDbfs: number;
  wdspMicPk: number;
  micAv: number;
  lvlrGr: number;
  cfcGr: number;
  compPk: number;
  compAv: number;
  alcGr: number;
  outPk: number;
  outAv: number;
  swr: number;
  psEnabled: boolean;
  psCorrecting: boolean;
  psFeedbackLevel: number;
  psCalState: number;
  psCalibrationStalled: boolean;
  targetSpectralDensity?: number;
  audioSuiteInputDbfs?: number;
  audioSuiteOutputDbfs?: number;
  audioSuiteMode?: 'native' | 'vst' | string;
  audioSuiteBypassed?: boolean;
  vstEngineActive?: boolean;
  vstDegradedDelta?: number | null;
  ingestDroppedFrameDelta?: number | null;
  p2QueuedPackets?: number | null;
  p2TransportFailureDelta?: number | null;
  p2QueueFailureDelta?: number | null;
  micUplinkStatus?: string | null;
};

export type TxFidelityState = 'idle' | 'monitor' | 'tune' | 'under' | 'sweet' | 'hot' | 'clip';
export type TxDensityStatus = 'unknown' | 'thin' | 'matched' | 'forced';
export type TxCrestStatus = 'unknown' | 'open' | 'controlled' | 'pinched';
export type TxFidelityActionTone = 'neutral' | 'raise' | 'reduce' | 'protect';
export type TxFidelityMetricStatus = 'idle' | 'met' | 'warn' | 'bad';

export type TxFidelityTuningMetric = {
  id: string;
  label: string;
  value: string;
  status: TxFidelityMetricStatus;
  target: string;
  detail: string;
};

export type TxFidelityAnalysis = {
  state: TxFidelityState;
  label: string;
  detail: string;
  recommendation: string;
  actionTone: TxFidelityActionTone;
  score: number;
  micDbfs: number | null;
  alcGr: number;
  lvlrGr: number;
  cfcGr: number;
  outDbfs: number | null;
  micCrestDb: number | null;
  compDbfs: number | null;
  compCrestDb: number | null;
  outCrestDb: number | null;
  audioSuiteInputDbfs: number | null;
  audioSuiteOutputDbfs: number | null;
  audioSuiteLabel: string;
  crestStatus: TxCrestStatus;
  swr: number;
  psFeedbackLevel: number | null;
  targetSpectralDensity: number;
  cleanSpectralDensityTarget: number;
  liveSpectralDensity: number | null;
  densityFit: number | null;
  densityStatus: TxDensityStatus;
  tuningMetrics: TxFidelityTuningMetric[];
  activeTargets: number;
  targetsMet: number;
  allTargetsMet: boolean;
};

function validDbfs(v: number): boolean {
  return Number.isFinite(v) && v > BYPASSED_DBFS_THRESHOLD;
}

function gainReductionDb(v: number): number {
  return Number.isFinite(v) && v > 0 ? v : 0;
}

function clamp(v: number, min: number, max: number): number {
  if (!Number.isFinite(v)) return min;
  return Math.max(min, Math.min(max, v));
}

function clampScore(v: number): number {
  return Math.max(0, Math.min(100, Math.round(v)));
}

function pctBetween(v: number, low: number, high: number): number {
  return clamp(((v - low) / (high - low)) * 100, 0, 100);
}

export function cleanSpectralDensityTarget(targetSpectralDensity: number): number {
  const target = clampScore(targetSpectralDensity);
  if (target <= DEFAULT_SPECTRAL_DENSITY_TARGET) return target;
  const lift =
    (target - DEFAULT_SPECTRAL_DENSITY_TARGET) /
    (100 - DEFAULT_SPECTRAL_DENSITY_TARGET);
  return clampScore(
    DEFAULT_SPECTRAL_DENSITY_TARGET +
      lift * (CLEAN_SPECTRAL_DENSITY_CEILING - DEFAULT_SPECTRAL_DENSITY_TARGET),
  );
}

function validCount(v: number | null | undefined): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : null;
}

function fmtDb(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dBFS`;
}

function fmtDbPlain(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dB`;
}

function fmtCount(v: number | null): string {
  return v === null ? '--' : `${Math.round(v)}`;
}

function metric(
  id: string,
  label: string,
  value: string,
  status: TxFidelityMetricStatus,
  target: string,
  detail: string,
): TxFidelityTuningMetric {
  return { id, label, value, status, target, detail };
}

function idleMetric(
  id: string,
  label: string,
  target: string,
  detail = 'Waiting for live TX or Preview telemetry.',
): TxFidelityTuningMetric {
  return metric(id, label, '--', 'idle', target, detail);
}

function dbWindowMetric(
  id: string,
  label: string,
  value: number | null,
  target: string,
  greenLow: number,
  greenHigh: number,
  warnLow: number,
  warnHigh: number,
  badHigh: number,
): TxFidelityTuningMetric {
  if (value === null) return idleMetric(id, label, target);
  let status: TxFidelityMetricStatus = 'warn';
  let detail = `${label} is outside the target window.`;
  if (value >= badHigh) {
    status = 'bad';
    detail = `${label} has too little headroom.`;
  } else if (value >= greenLow && value <= greenHigh) {
    status = 'met';
    detail = `${label} is in the target window.`;
  } else if (value < warnLow || value > warnHigh) {
    status = 'warn';
    detail = value < warnLow ? `${label} is low for the density target.` : `${label} is hot.`;
  }
  return metric(id, label, fmtDb(value), status, target, detail);
}

function grWindowMetric(
  id: string,
  label: string,
  value: number,
  target: string,
  greenLow: number,
  greenHigh: number,
  warnHigh: number,
  badHigh: number,
): TxFidelityTuningMetric {
  const v = gainReductionDb(value);
  let status: TxFidelityMetricStatus = 'warn';
  let detail = `${label} gain reduction is outside the target window.`;
  if (v >= badHigh) {
    status = 'bad';
    detail = `${label} gain reduction is excessive.`;
  } else if (v >= greenLow && v <= greenHigh) {
    status = 'met';
    detail = `${label} gain reduction is in the target window.`;
  } else if (v <= warnHigh) {
    status = 'warn';
    detail = `${label} gain reduction is usable but not ideal.`;
  }
  return metric(id, label, fmtDbPlain(v), status, target, detail);
}

function countDeltaMetric(
  id: string,
  label: string,
  value: number | null | undefined,
  target: string,
  warnHigh = 1,
): TxFidelityTuningMetric {
  const v = validCount(value);
  if (v === null) return idleMetric(id, label, target, 'Waiting for backend health diagnostics.');
  if (v <= 0) {
    return metric(id, label, '+0', 'met', target, `${label} is clean this poll.`);
  }
  return metric(
    id,
    label,
    `+${fmtCount(v)}`,
    v <= warnHigh ? 'warn' : 'bad',
    target,
    `${label} increased during the last diagnostic poll.`,
  );
}

function countCeilingMetric(
  id: string,
  label: string,
  value: number | null | undefined,
  target: string,
  warnHigh = 0,
): TxFidelityTuningMetric {
  const v = validCount(value);
  if (v === null) return idleMetric(id, label, target, 'Waiting for backend health diagnostics.');
  if (v <= warnHigh) {
    return metric(id, label, fmtCount(v), 'met', target, `${label} is in the target window.`);
  }
  return metric(
    id,
    label,
    fmtCount(v),
    v <= warnHigh + 2 ? 'warn' : 'bad',
    target,
    `${label} indicates transport pressure.`,
  );
}

function estimateLiveSpectralDensity(
  micDbfs: number | null,
  outDbfs: number | null,
  micCrestDb: number | null,
  compCrestDb: number | null,
  outCrestDb: number | null,
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
): number | null {
  if (micDbfs === null) return null;
  const micDensity = pctBetween(micDbfs, -36, -6);
  const outDensity = outDbfs === null ? micDensity : pctBetween(outDbfs, -24, -4);
  const dynamicsDensity = clamp(alcGr * 5 + lvlrGr * 3 + cfcGr * 2.5, 0, 100);
  const crestDb = outCrestDb ?? compCrestDb ?? micCrestDb;
  if (crestDb === null) {
    return clampScore(micDensity * 0.3 + outDensity * 0.25 + dynamicsDensity * 0.45);
  }
  const crestDensity = clamp(((24 - crestDb) / 18) * 100, 0, 100);
  return clampScore(
    micDensity * 0.22 +
    outDensity * 0.2 +
    dynamicsDensity * 0.38 +
    crestDensity * 0.2,
  );
}

function densityStatus(
  liveSpectralDensity: number | null,
  cleanTargetSpectralDensity: number,
  micDbfs: number | null,
  outDbfs: number | null,
  compCrestDb: number | null,
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
  audioSuiteOutputDbfs: number | null,
): TxDensityStatus {
  if (liveSpectralDensity === null) return 'unknown';
  if (liveSpectralDensity < cleanTargetSpectralDensity - 18) return 'thin';

  const forcedByLimiter =
    (micDbfs !== null && micDbfs > -3) ||
    (outDbfs !== null && outDbfs > -2) ||
    (audioSuiteOutputDbfs !== null && audioSuiteOutputDbfs > -1) ||
    (compCrestDb !== null && compCrestDb < 5) ||
    alcGr > 10 ||
    lvlrGr > 10 ||
    cfcGr > 7;
  const forcedByTargetOvershoot =
    liveSpectralDensity > cleanTargetSpectralDensity + 30 &&
    (alcGr > 7 || lvlrGr > 8 || cfcGr > 6);

  if (forcedByLimiter || forcedByTargetOvershoot) return 'forced';
  return 'matched';
}

function crestDb(peak: number | null, avg: number | null): number | null {
  if (peak === null || avg === null || avg > peak) return null;
  return peak - avg;
}

function classifyCrest(
  micCrestDb: number | null,
  compCrestDb: number | null,
  outCrestDb: number | null,
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
): TxCrestStatus {
  const c = outCrestDb ?? compCrestDb ?? micCrestDb;
  if (c === null) return 'unknown';
  if (c > 18) return 'open';
  if (c < 6 || (c < 8 && (alcGr > 8 || lvlrGr > 8 || cfcGr > 6))) return 'pinched';
  return 'controlled';
}

function audioSuiteLabel(s: TxFidelitySnapshot): string {
  return s.audioSuiteMode === 'vst' || s.vstEngineActive ? 'VST chain' : 'Audio Suite';
}

function recommendAudioSuiteHeadroomAction(s: TxFidelitySnapshot): string {
  return s.audioSuiteMode === 'vst' || s.vstEngineActive
    ? 'Lower VST/plugin output trim before raising mic or drive'
    : 'Lower Audio Suite output trim before raising mic or drive';
}

function buildTuningMetrics(args: {
  snapshot: TxFidelitySnapshot;
  activeVoiceChain: boolean;
  keyed: boolean;
  audioSuiteMetered: boolean;
  micDbfs: number | null;
  outDbfs: number | null;
  audioSuiteOutputDbfs: number | null;
  chainLabel: string;
  crestDb: number | null;
  crest: TxCrestStatus;
  alcGr: number;
  lvlrGr: number;
  cfcGr: number;
  swr: number;
  psFeedbackLevel: number | null;
  psEnabled: boolean;
  targetSpectralDensity: number;
  cleanSpectralDensityTarget: number;
  liveSpectralDensity: number | null;
  densityFit: number | null;
  density: TxDensityStatus;
}): TxFidelityTuningMetric[] {
  const {
    snapshot,
    activeVoiceChain,
    keyed,
    audioSuiteMetered,
    micDbfs,
    outDbfs,
    audioSuiteOutputDbfs,
    chainLabel,
    crestDb: liveCrestDb,
    crest,
    alcGr,
    lvlrGr,
    cfcGr,
    swr,
    psFeedbackLevel,
    psEnabled,
    targetSpectralDensity,
    cleanSpectralDensityTarget: cleanTargetSpectralDensity,
    liveSpectralDensity,
    densityFit,
    density,
  } = args;

  const metrics: TxFidelityTuningMetric[] = [];
  const activeTarget = activeVoiceChain ? 'live voice TX/Preview' : 'live voice TX/Preview required';

  metrics.push(
    activeVoiceChain
      ? dbWindowMetric('mic', 'MIC', micDbfs, '-18..-6 dBFS peak', -18, -6, -30, -3, -0.1)
      : idleMetric('mic', 'MIC', '-18..-6 dBFS peak', activeTarget),
  );
  metrics.push(
    activeVoiceChain
      ? dbWindowMetric('out', 'OUT', outDbfs, '-10..-3 dBFS peak', -10, -3, -24, -1, -0.05)
      : idleMetric('out', 'OUT', '-10..-3 dBFS peak', activeTarget),
  );

  if (audioSuiteMetered) {
    metrics.push(
      dbWindowMetric(
        chainLabel === 'VST chain' ? 'vstout' : 'asout',
        chainLabel === 'VST chain' ? 'VSTOUT' : 'ASOUT',
        audioSuiteOutputDbfs,
        '-12..-3 dBFS peak',
        -12,
        -3,
        -30,
        -1,
        -0.05,
      ),
    );
  }

  const densityTargetLabel =
    cleanTargetSpectralDensity === targetSpectralDensity
      ? `target ${targetSpectralDensity}`
      : `clean ${cleanTargetSpectralDensity} / profile ${targetSpectralDensity}`;

  if (!activeVoiceChain || liveSpectralDensity === null || densityFit === null) {
    metrics.push(idleMetric('dens', 'DENS', densityTargetLabel, activeTarget));
  } else if (density === 'forced') {
    metrics.push(metric(
      'dens',
      'DENS',
      `${liveSpectralDensity.toFixed(0)}/${cleanTargetSpectralDensity}`,
      'bad',
      `${densityTargetLabel} without forced compression`,
      'Density is being forced by clipping, limiting, or heavy compression.',
    ));
  } else if (density === 'matched' && densityFit >= 80) {
    metrics.push(metric(
      'dens',
      'DENS',
      `${liveSpectralDensity.toFixed(0)}/${cleanTargetSpectralDensity}`,
      'met',
      densityTargetLabel,
      'Live density is matched to the clean station target without over-compression.',
    ));
  } else {
    metrics.push(metric(
      'dens',
      'DENS',
      `${liveSpectralDensity.toFixed(0)}/${cleanTargetSpectralDensity}`,
      'warn',
      densityTargetLabel,
      density === 'thin'
        ? 'Live density is below the clean station target.'
        : 'Live density is near the clean target but not centered yet.',
    ));
  }

  if (!activeVoiceChain || liveCrestDb === null) {
    metrics.push(idleMetric('crest', 'CREST', '7..14 dB'));
  } else if (crest === 'pinched' || liveCrestDb < 6) {
    metrics.push(metric('crest', 'CREST', fmtDbPlain(liveCrestDb), 'bad', '7..14 dB', 'Crest factor is pinched by processing.'));
  } else if (liveCrestDb >= 7 && liveCrestDb <= 14) {
    metrics.push(metric('crest', 'CREST', fmtDbPlain(liveCrestDb), 'met', '7..14 dB', 'Crest factor is controlled and still natural.'));
  } else {
    metrics.push(metric(
      'crest',
      'CREST',
      fmtDbPlain(liveCrestDb),
      'warn',
      '7..14 dB',
      crest === 'open' ? 'Crest factor is too open for max clean density.' : 'Crest factor is outside the target window.',
    ));
  }

  metrics.push(
    activeVoiceChain
      ? grWindowMetric('alc', 'ALC', alcGr, '1..6 dB GR', 1, 6, 8, 11)
      : idleMetric('alc', 'ALC', '1..6 dB GR', activeTarget),
  );
  metrics.push(
    activeVoiceChain
      ? grWindowMetric('lvl', 'LVL', lvlrGr, '2..6 dB GR', 2, 6, 8, 10)
      : idleMetric('lvl', 'LVL', '2..6 dB GR', activeTarget),
  );
  metrics.push(
    activeVoiceChain
      ? grWindowMetric('cfc', 'CFC', cfcGr, '0..5 dB GR', 0, 5, 7, 8)
      : idleMetric('cfc', 'CFC', '0..5 dB GR', activeTarget),
  );

  if (swr >= 2.5) {
    metrics.push(metric('swr', 'SWR', swr.toFixed(2), 'bad', '< 1.8', 'RF match is unsafe for fidelity tuning.'));
  } else if (swr < 1.8) {
    metrics.push(metric('swr', 'SWR', swr.toFixed(2), 'met', '< 1.8', 'RF match is in the target window.'));
  } else {
    metrics.push(metric('swr', 'SWR', swr.toFixed(2), 'warn', '< 1.8', 'RF match is usable but not ideal.'));
  }

  if (psEnabled && keyed) {
    if (psFeedbackLevel === null) {
      metrics.push(idleMetric('psfb', 'PSFB', '128..181', 'Waiting for PureSignal feedback.'));
    } else if (psFeedbackLevel >= 128 && psFeedbackLevel <= 181) {
      metrics.push(metric('psfb', 'PSFB', fmtCount(psFeedbackLevel), 'met', '128..181', 'PureSignal feedback is in range.'));
    } else {
      metrics.push(metric(
        'psfb',
        'PSFB',
        fmtCount(psFeedbackLevel),
        psFeedbackLevel >= 96 && psFeedbackLevel <= 210 ? 'warn' : 'bad',
        '128..181',
        'PureSignal feedback is outside the target window.',
      ));
    }
  } else {
    metrics.push(idleMetric('psfb', 'PSFB', '128..181', 'PureSignal feedback is evaluated while keyed.'));
  }

  const vstRelevant = snapshot.audioSuiteMode === 'vst' || snapshot.vstEngineActive === true;
  if (vstRelevant) {
    metrics.push(countDeltaMetric('vstbuf', 'VSTBUF', snapshot.vstDegradedDelta, '0 degraded blocks / poll'));
  }
  const p2FailureDelta =
    (snapshot.p2TransportFailureDelta === null || snapshot.p2TransportFailureDelta === undefined) &&
    (snapshot.p2QueueFailureDelta === null || snapshot.p2QueueFailureDelta === undefined)
      ? null
      : (snapshot.p2TransportFailureDelta ?? 0) + (snapshot.p2QueueFailureDelta ?? 0);
  metrics.push(countDeltaMetric('drop', 'DROP', snapshot.ingestDroppedFrameDelta, '0 dropped frames / poll', 0));
  metrics.push(countCeilingMetric('p2q', 'P2Q', snapshot.p2QueuedPackets, '0 queued packets'));
  metrics.push(countDeltaMetric('p2fail', 'P2FAIL', p2FailureDelta, '0 transport failures / poll', 0));

  if (snapshot.micUplinkStatus) {
    const live = snapshot.micUplinkStatus === 'live';
    const unavailable =
      snapshot.micUplinkStatus === 'unavailable' || snapshot.micUplinkStatus === 'unknown';
    metrics.push(metric(
      'uplink',
      'UPLINK',
      snapshot.micUplinkStatus.toUpperCase(),
      live ? 'met' : unavailable || snapshot.micUplinkStatus === 'waiting-for-mic' ? 'idle' : 'warn',
      'LIVE',
      live ? 'Mic uplink is live.' : 'Mic uplink is not delivering fresh speech frames.',
    ));
  }

  return metrics;
}

export function analyzeTxFidelity(s: TxFidelitySnapshot): TxFidelityAnalysis {
  const keyed = s.moxOn || s.tunOn;
  const previewing = s.txMonitorEnabled && !keyed;
  const micDbfs = validDbfs(s.wdspMicPk)
    ? s.wdspMicPk
    : validDbfs(s.micDbfs)
      ? s.micDbfs
      : null;
  const alcGr = gainReductionDb(s.alcGr);
  const lvlrGr = gainReductionDb(s.lvlrGr);
  const cfcGr = gainReductionDb(s.cfcGr);
  const outDbfs = validDbfs(s.outPk) ? s.outPk : null;
  const micAvgDbfs = validDbfs(s.micAv) ? s.micAv : null;
  const compDbfs = validDbfs(s.compPk) ? s.compPk : null;
  const compAvgDbfs = validDbfs(s.compAv) ? s.compAv : null;
  const outAvgDbfs = validDbfs(s.outAv) ? s.outAv : null;
  const audioSuiteInputDbfs = validDbfs(s.audioSuiteInputDbfs ?? Number.NEGATIVE_INFINITY)
    ? s.audioSuiteInputDbfs!
    : null;
  const audioSuiteOutputDbfs = validDbfs(s.audioSuiteOutputDbfs ?? Number.NEGATIVE_INFINITY)
    ? s.audioSuiteOutputDbfs!
    : null;
  const chainLabel = audioSuiteLabel(s);
  const micCrestDb = crestDb(micDbfs, micAvgDbfs);
  const compCrestDb = crestDb(compDbfs, compAvgDbfs);
  const outCrestDb = crestDb(outDbfs, outAvgDbfs);
  const crest = classifyCrest(micCrestDb, compCrestDb, outCrestDb, alcGr, lvlrGr, cfcGr);
  const swr = Number.isFinite(s.swr) && s.swr > 0 ? s.swr : 1;
  const psFeedbackLevel =
    Number.isFinite(s.psFeedbackLevel) && s.psFeedbackLevel > 0 ? s.psFeedbackLevel : null;
  const targetSpectralDensity = clampScore(
    Number.isFinite(s.targetSpectralDensity)
      ? s.targetSpectralDensity ?? DEFAULT_SPECTRAL_DENSITY_TARGET
      : DEFAULT_SPECTRAL_DENSITY_TARGET,
  );
  const cleanTargetSpectralDensity = cleanSpectralDensityTarget(targetSpectralDensity);
  const activeVoiceChain = !s.tunOn && (keyed || previewing);
  const audioSuiteMetered =
    activeVoiceChain &&
    s.audioSuiteBypassed !== true &&
    (s.audioSuiteMode !== 'vst' || s.vstEngineActive === true);
  const meteredAudioSuiteOutputDbfs = audioSuiteMetered ? audioSuiteOutputDbfs : null;
  const liveSpectralDensity = activeVoiceChain
    ? estimateLiveSpectralDensity(
        micDbfs,
        outDbfs,
        micCrestDb,
        compCrestDb,
        outCrestDb,
        alcGr,
        lvlrGr,
        cfcGr,
      )
    : null;
  const densityFit =
    liveSpectralDensity === null
      ? null
      : clampScore(100 - Math.abs(liveSpectralDensity - cleanTargetSpectralDensity) * 1.6);
  const density = densityStatus(
    liveSpectralDensity,
    cleanTargetSpectralDensity,
    micDbfs,
    outDbfs,
    compCrestDb,
    alcGr,
    lvlrGr,
    cfcGr,
    meteredAudioSuiteOutputDbfs,
  );
  const liveCrestDb = outCrestDb ?? compCrestDb ?? micCrestDb;
  const tuningMetrics = buildTuningMetrics({
    snapshot: s,
    activeVoiceChain,
    keyed,
    audioSuiteMetered,
    micDbfs,
    outDbfs,
    audioSuiteOutputDbfs: meteredAudioSuiteOutputDbfs,
    chainLabel,
    crestDb: liveCrestDb,
    crest,
    alcGr,
    lvlrGr,
    cfcGr,
    swr,
    psFeedbackLevel,
    psEnabled: s.psEnabled,
    targetSpectralDensity,
    cleanSpectralDensityTarget: cleanTargetSpectralDensity,
    liveSpectralDensity,
    densityFit,
    density,
  });
  const activeTargets = tuningMetrics.filter((m) => m.status !== 'idle').length;
  const targetsMet = tuningMetrics.filter((m) => m.status === 'met').length;
  const allTargetsMet = activeTargets > 0 && activeTargets === targetsMet;

  const baseMetrics = {
    micDbfs,
    alcGr,
    lvlrGr,
    cfcGr,
    outDbfs,
    micCrestDb,
    compDbfs,
    compCrestDb,
    outCrestDb,
    audioSuiteInputDbfs: audioSuiteMetered ? audioSuiteInputDbfs : null,
    audioSuiteOutputDbfs: meteredAudioSuiteOutputDbfs,
    audioSuiteLabel: chainLabel,
    crestStatus: crest,
    swr,
    psFeedbackLevel,
    targetSpectralDensity,
    cleanSpectralDensityTarget: cleanTargetSpectralDensity,
    liveSpectralDensity,
    densityFit,
    densityStatus: density,
    tuningMetrics,
    activeTargets,
    targetsMet,
    allTargetsMet,
  };

  if (s.tunOn) {
    return {
      state: 'tune',
      label: 'Tune carrier',
      detail: 'Voice-chain fidelity is evaluated during MOX or Preview.',
      recommendation: 'Use MOX or Preview for voice-chain metering',
      actionTone: 'neutral',
      score: 0,
      ...baseMetrics,
    };
  }

  if (!keyed && !previewing) {
    return {
      state: 'idle',
      label: 'Off-air ready',
      detail: s.txMonitorEnabled
        ? 'Preview is armed; speak to meter the chain without RF.'
        : 'Enable Preview or key MOX to meter station fidelity.',
      recommendation: s.txMonitorEnabled
        ? 'Speak into the mic to meter the TX chain'
        : 'Enable Preview before adjusting speech processing',
      actionTone: 'neutral',
      score: 0,
      ...baseMetrics,
    };
  }

  let score = 100;
  const reasons: string[] = [];
  const stateBase: TxFidelityState = previewing ? 'monitor' : 'sweet';

  if (micDbfs === null) {
    score -= 30;
    reasons.push('No usable mic peak yet');
  } else if (
    micDbfs >= 0 ||
    (outDbfs !== null && outDbfs >= 0) ||
    (meteredAudioSuiteOutputDbfs !== null && meteredAudioSuiteOutputDbfs >= -0.05)
  ) {
    const audioSuiteAtCeiling =
      micDbfs < 0 &&
      (outDbfs === null || outDbfs < 0) &&
      meteredAudioSuiteOutputDbfs !== null &&
      meteredAudioSuiteOutputDbfs >= -0.05;
    return {
      state: 'clip',
      label: 'Clip risk',
      detail: audioSuiteAtCeiling
        ? `${chainLabel} output is reaching full scale.`
        : 'Back down mic gain or drive; peaks are reaching full scale.',
      recommendation: audioSuiteAtCeiling
        ? recommendAudioSuiteHeadroomAction(s)
        : 'Back down mic gain or drive now',
      actionTone: 'protect',
      score: 10,
      ...baseMetrics,
    };
  } else if (micDbfs > -3) {
    score -= 35;
    reasons.push('Mic peak is hot');
  } else if (micDbfs < -30) {
    score -= 30;
    reasons.push('Mic peak is low');
  } else if (micDbfs < -18) {
    score -= 12;
    reasons.push('Mic can come up slightly');
  }

  if (outDbfs !== null) {
    if (outDbfs > -1) {
      score -= 40;
      reasons.push('TX output has almost no headroom');
    } else if (outDbfs > -3) {
      score -= 20;
      reasons.push('TX output headroom is tight');
    }
  }

  if (meteredAudioSuiteOutputDbfs !== null) {
    if (meteredAudioSuiteOutputDbfs > -1) {
      score -= 36;
      reasons.push(`${chainLabel} output has almost no headroom`);
    } else if (meteredAudioSuiteOutputDbfs > -3) {
      score -= 18;
      reasons.push(`${chainLabel} output headroom is tight`);
    }

    if (
      audioSuiteInputDbfs !== null &&
      audioSuiteInputDbfs < -8 &&
      meteredAudioSuiteOutputDbfs > -1
    ) {
      score -= 8;
      reasons.push(`${chainLabel} is adding excessive gain`);
    }
  }

  if (alcGr > 11) {
    score -= 35;
    reasons.push('ALC is limiting hard');
  } else if (alcGr > 8) {
    score -= 18;
    reasons.push('ALC is above the broadcast comfort zone');
  } else if (alcGr < 1 && keyed) {
    score -= 10;
    reasons.push('ALC is barely working');
  }

  if (lvlrGr > 10) {
    score -= 18;
    reasons.push('Leveler is pulling too much');
  }

  if (cfcGr > 7) {
    score -= 12;
    reasons.push('CFC compression is heavy');
  }

  if (liveSpectralDensity !== null && densityFit !== null) {
    if (density === 'thin') {
      const cleanShortfall = cleanTargetSpectralDensity - liveSpectralDensity;
      score -= Math.min(24, Math.max(8, cleanShortfall * 0.6));
      reasons.push('TX density is below clean profile target');
    } else if (density === 'forced') {
      const overshoot = Math.max(0, liveSpectralDensity - cleanTargetSpectralDensity);
      score -= Math.min(24, Math.max(10, overshoot * 0.4));
      reasons.push('Density is forced by compression');
    } else if (densityFit < 80) {
      score -= 8;
      reasons.push(
        liveSpectralDensity < cleanTargetSpectralDensity
          ? 'TX density is near but below clean profile target'
          : 'TX density is near but above clean profile target',
      );
    }
  }

  if (activeVoiceChain) {
    if (crest === 'open') {
      score -= 10;
      reasons.push('Crest factor is too open for the density target');
    } else if (crest === 'pinched') {
      score -= 16;
      reasons.push(
        compCrestDb !== null && compCrestDb < 6
          ? 'Compressor crest is pinched'
          : 'Crest factor is pinched by processing',
      );
    }
  }

  if (swr >= 3) {
    score -= 40;
    reasons.push('SWR protection risk');
  } else if (swr >= 2) {
    score -= 15;
    reasons.push('SWR is elevated');
  }

  if (s.psEnabled && keyed) {
    if (s.psCalibrationStalled) {
      score -= 40;
      reasons.push('PureSignal calibration stalled');
    } else if (!s.psCorrecting && s.psCalState > 0) {
      score -= 15;
      reasons.push('PureSignal is still fitting');
    } else if (!s.psCorrecting) {
      score -= 20;
      reasons.push('PureSignal is armed but not correcting');
    }

    if (psFeedbackLevel !== null && (psFeedbackLevel < 128 || psFeedbackLevel > 181)) {
      score -= 18;
      reasons.push(`PureSignal feedback ${psFeedbackLevel.toFixed(0)} outside 128..181`);
    }
  }

  const vstDegradedDelta = validCount(s.vstDegradedDelta);
  const ingestDroppedFrameDelta = validCount(s.ingestDroppedFrameDelta);
  const p2QueuedPackets = validCount(s.p2QueuedPackets);
  const p2TransportFailureDelta = validCount(s.p2TransportFailureDelta);
  const p2QueueFailureDelta = validCount(s.p2QueueFailureDelta);
  if ((s.audioSuiteMode === 'vst' || s.vstEngineActive === true) && vstDegradedDelta !== null && vstDegradedDelta > 0) {
    score -= vstDegradedDelta > 2 ? 35 : 18;
    reasons.push('VST engine degraded blocks increased');
  }
  if (ingestDroppedFrameDelta !== null && ingestDroppedFrameDelta > 0) {
    score -= 35;
    reasons.push('TX ingest dropped frames');
  }
  if (p2QueuedPackets !== null && p2QueuedPackets > 0) {
    score -= p2QueuedPackets > 2 ? 20 : 10;
    reasons.push('TX packet queue is backing up');
  }
  if (
    (p2TransportFailureDelta !== null && p2TransportFailureDelta > 0) ||
    (p2QueueFailureDelta !== null && p2QueueFailureDelta > 0)
  ) {
    score -= 35;
    reasons.push('TX transport failures increased');
  }
  if (
    s.micUplinkStatus &&
    s.micUplinkStatus !== 'live' &&
    s.micUplinkStatus !== 'unavailable' &&
    s.micUplinkStatus !== 'unknown' &&
    activeVoiceChain
  ) {
    score -= 20;
    reasons.push('Mic uplink is not live');
  }

  const finalScore = clampScore(score);
  const hasHotReason = reasons.some((r) =>
    r.includes('hot') ||
    r.includes('hard') ||
    r.includes('headroom') ||
    r.includes('stalled') ||
    r.includes('SWR protection') ||
    r.includes('forced') ||
    r.includes('pinched') ||
    r.includes('degraded') ||
    r.includes('dropped') ||
    r.includes('queue') ||
    r.includes('transport') ||
    r.includes('uplink')
  );
  const hasUnderReason = reasons.some(
    (r) =>
      r.includes('low') ||
      r.includes('below clean profile target') ||
      r.includes('near but below clean profile target') ||
      r.includes('too open'),
  );
  if (hasHotReason || (finalScore < 45 && !hasUnderReason)) {
    const recommendation = recommendHotAction(
      s,
      micDbfs,
      outDbfs,
      meteredAudioSuiteOutputDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
      compCrestDb,
      density,
      crest,
      swr,
      psFeedbackLevel,
    );
    return {
      state: 'hot',
      label: 'Too hot',
      detail: reasons.join(' · ') || 'Reduce mic gain or ALC drive.',
      recommendation,
      actionTone: recommendation.startsWith('Stop RF') || recommendation.startsWith('Correct PureSignal')
        ? 'protect'
        : 'reduce',
      score: finalScore,
      ...baseMetrics,
    };
  }
  if (finalScore < 70 || hasUnderReason) {
    const recommendation = recommendUnderAction(micDbfs, alcGr, density, crest);
    return {
      state: 'under',
      label: 'Under-driven',
      detail: reasons.join(' · ') || 'Raise mic gain until voice peaks sit around -12 to -6 dBFS.',
      recommendation,
      actionTone: 'raise',
      score: finalScore,
      ...baseMetrics,
    };
  }

  const ps = s.psEnabled
    ? s.psCorrecting
      ? 'PureSignal correcting'
      : 'PureSignal armed'
    : 'PureSignal off';
  return {
    state: stateBase,
    label: previewing ? 'Preview sweet spot' : 'Broadcast sweet spot',
    detail: allTargetsMet
      ? `All live TX fidelity targets are green; ${ps}.`
      : `Mic/ALC dynamics are in range; ${ps}.`,
    recommendation:
      allTargetsMet && targetSpectralDensity >= 95
        ? 'Hold; max clean spectral density is met'
        : s.psEnabled && s.psCorrecting
          ? 'Hold levels; PureSignal is correcting the PA'
          : 'Hold levels; keep peaks below clipping',
    actionTone: 'neutral',
    score: finalScore,
    ...baseMetrics,
  };
}

function recommendHotAction(
  s: TxFidelitySnapshot,
  micDbfs: number | null,
  outDbfs: number | null,
  audioSuiteOutputDbfs: number | null,
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
  compCrestDb: number | null,
  density: TxDensityStatus,
  crest: TxCrestStatus,
  swr: number,
  psFeedbackLevel: number | null,
): string {
  if ((s.vstDegradedDelta ?? 0) > 0) return 'Stabilize the VST engine before adding density';
  if ((s.ingestDroppedFrameDelta ?? 0) > 0) return 'Fix TX ingest drops before adding density';
  if (((s.p2TransportFailureDelta ?? 0) + (s.p2QueueFailureDelta ?? 0)) > 0) {
    return 'Fix TX transport failures before adding density';
  }
  if ((s.p2QueuedPackets ?? 0) > 0) return 'Reduce TX load until packet queue returns to zero';
  if (
    s.micUplinkStatus &&
    s.micUplinkStatus !== 'live' &&
    s.micUplinkStatus !== 'unavailable' &&
    s.micUplinkStatus !== 'unknown'
  ) {
    return 'Restore live mic uplink before tuning density';
  }
  if (swr >= 3) return 'Stop RF and check antenna match';
  if (s.psCalibrationStalled) return 'Correct PureSignal feedback before increasing drive';
  if (s.psEnabled && psFeedbackLevel !== null && psFeedbackLevel > 181) {
    return 'Add PS feedback attenuation or lower HW peak';
  }
  if (s.psEnabled && psFeedbackLevel !== null && psFeedbackLevel < 128) {
    return 'Reduce PS feedback attenuation or raise HW peak';
  }
  if (micDbfs !== null && micDbfs > -3) return 'Lower mic gain until peaks stay below -6 dBFS';
  if (audioSuiteOutputDbfs !== null && audioSuiteOutputDbfs > -3) {
    return recommendAudioSuiteHeadroomAction(s);
  }
  if (outDbfs !== null && outDbfs > -3) return 'Reduce drive or ALC max gain for TX output headroom';
  if (alcGr > 8) return 'Lower mic gain or ALC max gain';
  if (lvlrGr > 10) return 'Lower leveler max gain or slow the leveler';
  if (compCrestDb !== null && compCrestDb < 6) return 'Reduce compressor gain before raising drive';
  if (cfcGr > 7 || density === 'forced' || crest === 'pinched') {
    return 'Reduce CFC density before raising drive';
  }
  if (swr >= 2) return 'Reduce power and inspect the RF match';
  return 'Back off the hottest TX stage';
}

function recommendUnderAction(
  micDbfs: number | null,
  alcGr: number,
  density: TxDensityStatus,
  crest: TxCrestStatus,
): string {
  if (micDbfs === null) return 'Enable mic audio and verify the selected input';
  if (micDbfs < -30) return 'Raise mic gain toward -12 to -6 dBFS peaks';
  if (crest === 'open') return 'Add controlled speech density before adding RF drive';
  if (density === 'thin') return 'Increase mic gain or profile density before adding drive';
  if (alcGr < 1) return 'Raise mic gain until ALC works lightly';
  return 'Add controlled speech density, not RF drive';
}
