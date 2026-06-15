// SPDX-License-Identifier: GPL-2.0-or-later

import type {
  FrontendDspSceneDiagnosticsPayload,
  RxMode,
} from '../api/client';
import type { RxChainAnalysis } from '../dsp/rx-chain-health';
import type { SignalEnhanceSceneStatus } from '../dsp/signal-estimator';
import type { SmartNrStatus } from '../state/smart-nr-store';

let sessionClientId: string | null = null;

function frontendClientId(): string {
  if (sessionClientId) return sessionClientId;
  const random = globalThis.crypto?.randomUUID?.()
    ?? Math.random().toString(36).slice(2, 10);
  sessionClientId = `frontend-${random}`;
  return sessionClientId;
}

function n(v: number | null | undefined): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function latestIso(...values: Array<string | null | undefined>): string | null {
  let best: string | null = null;
  let bestMs = Number.NEGATIVE_INFINITY;
  for (const value of values) {
    if (typeof value !== 'string') continue;
    const ms = Date.parse(value);
    if (!Number.isFinite(ms) || ms <= bestMs) continue;
    best = value;
    bestMs = ms;
  }
  return best;
}

export function buildFrontendDspSceneDiagnosticsPayload(
  mode: RxMode,
  signal: SignalEnhanceSceneStatus | null,
  smart: SmartNrStatus | null,
  rxChain: RxChainAnalysis | null = null,
): FrontendDspSceneDiagnosticsPayload | null {
  const rxLabel = smart?.rxChainLabel ?? rxChain?.label ?? null;
  const rxRecommendation = smart?.rxChainRecommendation ?? rxChain?.recommendation ?? null;
  const rxTone = smart?.rxChainTone ?? rxChain?.actionTone ?? null;
  const rxScore = smart?.rxChainScore ?? rxChain?.score;

  return {
    sourceAtUtc: latestIso(signal?.atUtc, smart?.atUtc),
    sourceClientId: frontendClientId(),
    mode,
    signalProfile: signal?.profileId ?? null,
    signalReason: signal?.reason ?? null,
    smartNrProfile: smart?.profile ?? null,
    smartNrReason: smart?.reason ?? null,
    smartNrRecommendation: smart?.rxChainRecommendation ?? smart?.capabilityRecommendation ?? smart?.reason ?? rxRecommendation,
    smartNrHeldByRxChain: smart?.heldByRxChain ?? null,
    smartNrRxChainLabel: rxLabel,
    smartNrRxChainRecommendation: rxRecommendation,
    smartNrRxChainTone: rxTone,
    smartNrRxChainScore: n(rxScore),
    maxSnrDb: n(smart?.maxSnrDb ?? signal?.maxSnrDb),
    coherentMaxSnrDb: n(signal?.coherentMaxSnrDb),
    occupiedPct: n(smart?.occupancyPct ?? signal?.occupiedPct),
    coherentOccupiedPct: n(smart?.coherentOccupancyPct ?? signal?.coherentOccupiedPct),
    impulsivePct: n(smart?.impulsivePct ?? signal?.impulsivePct),
    peakCount: n(smart?.peakCount ?? signal?.peakCount),
    coherentPeakCount: n(smart?.coherentPeakCount ?? signal?.coherentPeakCount),
    coherentSubthresholdSignal: smart?.coherentSubthresholdSignal ?? null,
  };
}
