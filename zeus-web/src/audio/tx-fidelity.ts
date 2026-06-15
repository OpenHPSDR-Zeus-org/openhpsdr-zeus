// SPDX-License-Identifier: GPL-2.0-or-later

const BYPASSED_DBFS_THRESHOLD = -200;

export type TxFidelitySnapshot = {
  moxOn: boolean;
  tunOn: boolean;
  txMonitorEnabled: boolean;
  micDbfs: number;
  wdspMicPk: number;
  lvlrGr: number;
  cfcGr: number;
  alcGr: number;
  outPk: number;
  swr: number;
  psEnabled: boolean;
  psCorrecting: boolean;
  psFeedbackLevel: number;
  psCalState: number;
  psCalibrationStalled: boolean;
};

export type TxFidelityState = 'idle' | 'monitor' | 'tune' | 'under' | 'sweet' | 'hot' | 'clip';

export type TxFidelityAnalysis = {
  state: TxFidelityState;
  label: string;
  detail: string;
  score: number;
  micDbfs: number | null;
  alcGr: number;
  lvlrGr: number;
  cfcGr: number;
  outDbfs: number | null;
  swr: number;
  psFeedbackLevel: number | null;
};

function validDbfs(v: number): boolean {
  return Number.isFinite(v) && v > BYPASSED_DBFS_THRESHOLD;
}

function finiteOrZero(v: number): number {
  return Number.isFinite(v) ? v : 0;
}

function clampScore(v: number): number {
  return Math.max(0, Math.min(100, Math.round(v)));
}

export function analyzeTxFidelity(s: TxFidelitySnapshot): TxFidelityAnalysis {
  const keyed = s.moxOn || s.tunOn;
  const auditioning = s.txMonitorEnabled && !keyed;
  const micDbfs = validDbfs(s.wdspMicPk)
    ? s.wdspMicPk
    : validDbfs(s.micDbfs)
      ? s.micDbfs
      : null;
  const alcGr = finiteOrZero(s.alcGr);
  const lvlrGr = finiteOrZero(s.lvlrGr);
  const cfcGr = finiteOrZero(s.cfcGr);
  const outDbfs = validDbfs(s.outPk) ? s.outPk : null;
  const swr = Number.isFinite(s.swr) && s.swr > 0 ? s.swr : 1;
  const psFeedbackLevel =
    Number.isFinite(s.psFeedbackLevel) && s.psFeedbackLevel > 0 ? s.psFeedbackLevel : null;

  const baseMetrics = {
    micDbfs,
    alcGr,
    lvlrGr,
    cfcGr,
    outDbfs,
    swr,
    psFeedbackLevel,
  };

  if (s.tunOn) {
    return {
      state: 'tune',
      label: 'Tune carrier',
      detail: 'Voice-chain fidelity is evaluated during MOX or TX monitor.',
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
  if (
    finalScore < 45 ||
    reasons.some((r) =>
      r.includes('hot') ||
      r.includes('hard') ||
      r.includes('headroom') ||
      r.includes('stalled') ||
      r.includes('SWR protection')
    )
  ) {
    return {
      state: 'hot',
      label: 'Too hot',
      detail: reasons.join(' · ') || 'Reduce mic gain or ALC drive.',
      score: finalScore,
      ...baseMetrics,
    };
  }
  if (finalScore < 70 || reasons.some((r) => r.includes('low'))) {
    return {
      state: 'under',
      label: 'Under-driven',
      detail: reasons.join(' · ') || 'Raise mic gain until voice peaks sit around -12 to -6 dBFS.',
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
    score: finalScore,
    ...baseMetrics,
  };
}
