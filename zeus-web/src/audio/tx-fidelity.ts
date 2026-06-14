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
  psEnabled: boolean;
  psCorrecting: boolean;
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

  if (s.tunOn) {
    return {
      state: 'tune',
      label: 'Tune carrier',
      detail: 'Voice-chain fidelity is evaluated during MOX or TX monitor.',
      score: 0,
      micDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
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
      micDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
    };
  }

  let score = 100;
  const reasons: string[] = [];
  const stateBase: TxFidelityState = auditioning ? 'monitor' : 'sweet';

  if (micDbfs === null) {
    score -= 30;
    reasons.push('No usable mic peak yet');
  } else if (micDbfs >= 0 || (validDbfs(s.outPk) && s.outPk >= 0)) {
    return {
      state: 'clip',
      label: 'Clip risk',
      detail: 'Back down mic gain or drive; peaks are reaching full scale.',
      score: 10,
      micDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
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

  const finalScore = clampScore(score);
  if (finalScore < 45 || reasons.some((r) => r.includes('hot') || r.includes('hard'))) {
    return {
      state: 'hot',
      label: 'Too hot',
      detail: reasons.join(' · ') || 'Reduce mic gain or ALC drive.',
      score: finalScore,
      micDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
    };
  }
  if (finalScore < 70 || reasons.some((r) => r.includes('low'))) {
    return {
      state: 'under',
      label: 'Under-driven',
      detail: reasons.join(' · ') || 'Raise mic gain until voice peaks sit around -12 to -6 dBFS.',
      score: finalScore,
      micDbfs,
      alcGr,
      lvlrGr,
      cfcGr,
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
    micDbfs,
    alcGr,
    lvlrGr,
    cfcGr,
  };
}
