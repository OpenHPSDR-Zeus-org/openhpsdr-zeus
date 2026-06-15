// SPDX-License-Identifier: GPL-2.0-or-later

const BYPASSED_DBFS_THRESHOLD = -200;
const DEFAULT_SPECTRAL_DENSITY_TARGET = 55;

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
};

export type TxFidelityState = 'idle' | 'monitor' | 'tune' | 'under' | 'sweet' | 'hot' | 'clip';
export type TxDensityStatus = 'unknown' | 'thin' | 'matched' | 'forced';
export type TxCrestStatus = 'unknown' | 'open' | 'controlled' | 'pinched';
export type TxFidelityActionTone = 'neutral' | 'raise' | 'reduce' | 'protect';

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
  crestStatus: TxCrestStatus;
  swr: number;
  psFeedbackLevel: number | null;
  targetSpectralDensity: number;
  liveSpectralDensity: number | null;
  densityFit: number | null;
  densityStatus: TxDensityStatus;
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
  targetSpectralDensity: number,
  micDbfs: number | null,
  outDbfs: number | null,
  compCrestDb: number | null,
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
): TxDensityStatus {
  if (liveSpectralDensity === null) return 'unknown';
  if (liveSpectralDensity < targetSpectralDensity - 18) return 'thin';

  const forcedByLimiter =
    (micDbfs !== null && micDbfs > -3) ||
    (outDbfs !== null && outDbfs > -2) ||
    (compCrestDb !== null && compCrestDb < 5) ||
    alcGr > 10 ||
    lvlrGr > 10 ||
    cfcGr > 7;
  const forcedByTargetOvershoot =
    liveSpectralDensity > targetSpectralDensity + 30 &&
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

export function analyzeTxFidelity(s: TxFidelitySnapshot): TxFidelityAnalysis {
  const keyed = s.moxOn || s.tunOn;
  const auditioning = s.txMonitorEnabled && !keyed;
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
  const activeVoiceChain = !s.tunOn && (keyed || auditioning);
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
      : clampScore(100 - Math.abs(liveSpectralDensity - targetSpectralDensity) * 1.6);
  const density = densityStatus(
    liveSpectralDensity,
    targetSpectralDensity,
    micDbfs,
    outDbfs,
    compCrestDb,
    alcGr,
    lvlrGr,
    cfcGr,
  );

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
    crestStatus: crest,
    swr,
    psFeedbackLevel,
    targetSpectralDensity,
    liveSpectralDensity,
    densityFit,
    densityStatus: density,
  };

  if (s.tunOn) {
    return {
      state: 'tune',
      label: 'Tune carrier',
      detail: 'Voice-chain fidelity is evaluated during MOX or TX monitor.',
      recommendation: 'Use MOX or TX monitor for voice-chain metering',
      actionTone: 'neutral',
      score: 0,
      ...baseMetrics,
    };
  }

  if (!keyed && !auditioning) {
    return {
      state: 'idle',
      label: 'Off-air ready',
      detail: s.txMonitorEnabled
        ? 'TX monitor is armed; speak to meter the chain without RF.'
        : 'Enable TX monitor or key MOX to meter station fidelity.',
      recommendation: s.txMonitorEnabled
        ? 'Speak into the mic to meter the TX chain'
        : 'Enable TX monitor before adjusting speech processing',
      actionTone: 'neutral',
      score: 0,
      ...baseMetrics,
    };
  }

  let score = 100;
  const reasons: string[] = [];
  const stateBase: TxFidelityState = auditioning ? 'monitor' : 'sweet';

  if (micDbfs === null) {
    score -= 30;
    reasons.push('No usable mic peak yet');
  } else if (micDbfs >= 0 || (outDbfs !== null && outDbfs >= 0)) {
    return {
      state: 'clip',
      label: 'Clip risk',
      detail: 'Back down mic gain or drive; peaks are reaching full scale.',
      recommendation: 'Back down mic gain or drive now',
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
      const shortfall = targetSpectralDensity - liveSpectralDensity;
      score -= Math.min(24, Math.max(8, shortfall * 0.6));
      reasons.push('TX density is below profile target');
    } else if (density === 'forced') {
      const overshoot = Math.max(0, liveSpectralDensity - targetSpectralDensity);
      score -= Math.min(24, Math.max(10, overshoot * 0.4));
      reasons.push('Density is forced by compression');
    } else if (densityFit < 70) {
      score -= 8;
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

  const finalScore = clampScore(score);
  const hasHotReason = reasons.some((r) =>
    r.includes('hot') ||
    r.includes('hard') ||
    r.includes('headroom') ||
    r.includes('stalled') ||
    r.includes('SWR protection') ||
    r.includes('forced') ||
    r.includes('pinched')
  );
  const hasUnderReason = reasons.some(
    (r) => r.includes('low') || r.includes('below profile target') || r.includes('too open'),
  );
  if (hasHotReason || (finalScore < 45 && !hasUnderReason)) {
    const recommendation = recommendHotAction(
      s,
      micDbfs,
      outDbfs,
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
    label: auditioning ? 'Monitor sweet spot' : 'Broadcast sweet spot',
    detail: `Mic/ALC dynamics are in range; ${ps}.`,
    recommendation: s.psEnabled && s.psCorrecting
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
  alcGr: number,
  lvlrGr: number,
  cfcGr: number,
  compCrestDb: number | null,
  density: TxDensityStatus,
  crest: TxCrestStatus,
  swr: number,
  psFeedbackLevel: number | null,
): string {
  if (swr >= 3) return 'Stop RF and check antenna match';
  if (s.psCalibrationStalled) return 'Correct PureSignal feedback before increasing drive';
  if (s.psEnabled && psFeedbackLevel !== null && psFeedbackLevel > 181) {
    return 'Add PS feedback attenuation or lower HW peak';
  }
  if (s.psEnabled && psFeedbackLevel !== null && psFeedbackLevel < 128) {
    return 'Reduce PS feedback attenuation or raise HW peak';
  }
  if (micDbfs !== null && micDbfs > -3) return 'Lower mic gain until peaks stay below -6 dBFS';
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
