// SPDX-License-Identifier: GPL-2.0-or-later

const METER_SENTINEL_DB = -200;
const LEGACY_RX_FLOOR_DBM = -160;

export type RxChainSnapshot = {
  signalPk: number;
  signalAv: number;
  adcPk: number;
  adcAv: number;
  agcGain: number;
  agcEnvPk: number;
  agcEnvAv: number;
  fallbackDbm?: number;
};

export type RxSignalSource = 'rx-meters-v2' | 'legacy-rx-meter' | 'none';

export type RxChainState =
  | 'waiting'
  | 'quiet'
  | 'weak'
  | 'optimized'
  | 'underfilled'
  | 'agc-stressed'
  | 'overload';

export type RxChainAnalysis = {
  state: RxChainState;
  label: string;
  detail: string;
  score: number;
  signalDbm: number | null;
  signalSource: RxSignalSource;
  adcPk: number | null;
  adcAv: number | null;
  adcHeadroomDb: number | null;
  adcUtilizationPct: number | null;
  agcGain: number;
  agcEnvPk: number | null;
};

export function validMeterDb(v: number): boolean {
  return Number.isFinite(v) && v > METER_SENTINEL_DB;
}

function validLegacyDbm(v: number | undefined): boolean {
  return Number.isFinite(v) && (v as number) > LEGACY_RX_FLOOR_DBM;
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

export function preferredRxSignalDbm(s: RxChainSnapshot): {
  dbm: number | null;
  source: RxSignalSource;
} {
  if (validMeterDb(s.signalPk)) return { dbm: s.signalPk, source: 'rx-meters-v2' };
  if (validMeterDb(s.signalAv)) return { dbm: s.signalAv, source: 'rx-meters-v2' };
  if (validLegacyDbm(s.fallbackDbm)) {
    return { dbm: s.fallbackDbm as number, source: 'legacy-rx-meter' };
  }
  return { dbm: null, source: 'none' };
}

export function analyzeRxChain(s: RxChainSnapshot): RxChainAnalysis {
  const preferred = preferredRxSignalDbm(s);
  const signalDbm = preferred.dbm;
  const adcPk = validMeterDb(s.adcPk) ? s.adcPk : null;
  const adcAv = validMeterDb(s.adcAv) ? s.adcAv : null;
  const agcGain = Number.isFinite(s.agcGain) ? s.agcGain : 0;
  const agcEnvPk = validMeterDb(s.agcEnvPk) ? s.agcEnvPk : null;
  const adcHeadroomDb = adcPk === null ? null : Math.max(0, -adcPk);
  const adcUtilizationPct = adcPk === null ? null : pctBetween(adcPk, -96, -3);
  const baseMetrics = {
    signalDbm,
    signalSource: preferred.source,
    adcPk,
    adcAv,
    adcHeadroomDb,
    adcUtilizationPct,
    agcGain,
    agcEnvPk,
  };

  if (signalDbm === null && adcPk === null) {
    return {
      state: 'waiting',
      label: 'RX telemetry waiting',
      detail: 'Waiting for calibrated signal, ADC, and AGC stage meters.',
      score: 0,
      ...baseMetrics,
    };
  }

  if (preferred.source === 'legacy-rx-meter' && adcPk === null) {
    return {
      state: 'waiting',
      label: 'RX signal only',
      detail: 'Using the legacy S-meter while waiting for calibrated ADC and AGC stage meters.',
      score: 0,
      ...baseMetrics,
    };
  }

  if (adcPk !== null && adcPk > -3) {
    return {
      state: 'overload',
      label: 'ADC overload risk',
      detail: adcPk > -1
        ? 'ADC peaks are nearly full scale; add attenuation or reduce RF gain before weak signals are masked.'
        : 'ADC headroom is tight; leave more front-end margin for impulse noise and strong adjacent signals.',
      score: adcPk > -1 ? 12 : 32,
      ...baseMetrics,
    };
  }

  let score = 100;
  const reasons: string[] = [];

  if (adcPk !== null) {
    if (adcPk > -6) {
      score -= 22;
      reasons.push('ADC headroom is tight');
    } else if (adcPk < -76 && signalDbm !== null && signalDbm > -112) {
      score -= 28;
      reasons.push('ADC is under-filled for the received signal');
    } else if (adcPk < -88) {
      score -= 12;
      reasons.push('ADC trace is near the floor');
    }
  }

  if (agcGain > 45) {
    score -= 22;
    reasons.push('AGC is applying heavy boost');
  } else if (agcGain > 32) {
    score -= 10;
    reasons.push('AGC boost is high');
  } else if (agcGain < -18) {
    score -= 22;
    reasons.push('AGC is cutting deeply');
  } else if (agcGain < -10) {
    score -= 10;
    reasons.push('AGC is cutting the signal');
  }

  if (signalDbm !== null) {
    if (signalDbm < -124) {
      score -= 12;
      reasons.push('Signal is at the practical S-meter floor');
    } else if (signalDbm < -112 && agcGain > 30) {
      score -= 8;
      reasons.push('Weak-signal copy is noise limited');
    }
  }

  const finalScore = clampScore(score);

  if (reasons.some((r) => r.includes('under-filled'))) {
    return {
      state: 'underfilled',
      label: 'Front end under-filled',
      detail: reasons.join(' · '),
      score: finalScore,
      ...baseMetrics,
    };
  }

  if (reasons.some((r) => r.includes('AGC'))) {
    return {
      state: 'agc-stressed',
      label: 'AGC stressed',
      detail: reasons.join(' · '),
      score: finalScore,
      ...baseMetrics,
    };
  }

  if (signalDbm !== null && signalDbm < -110) {
    return {
      state: 'weak',
      label: 'Weak-signal posture',
      detail: reasons.join(' · ') || 'ADC headroom is preserved while tracking a weak signal.',
      score: finalScore,
      ...baseMetrics,
    };
  }

  if (signalDbm === null || signalDbm <= -130) {
    return {
      state: 'quiet',
      label: 'Quiet receiver',
      detail: reasons.join(' · ') || 'Receiver chain is idle or below calibrated S-meter floor.',
      score: finalScore,
      ...baseMetrics,
    };
  }

  return {
    state: 'optimized',
    label: 'RX chain optimized',
    detail: reasons.join(' · ') || 'ADC headroom and AGC gain are in range.',
    score: finalScore,
    ...baseMetrics,
  };
}
