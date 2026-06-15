// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { SignalEnhanceSceneStatus } from '../dsp/signal-estimator';
import type { SmartNrStatus } from '../state/smart-nr-store';
import { NR_CONFIG_DEFAULT } from '../api/client';
import { buildFrontendDspSceneDiagnosticsPayload } from './dspSceneDiagnosticsPayload';

describe('buildFrontendDspSceneDiagnosticsPayload', () => {
  it('mirrors Signal Intelligence and Smart NR evidence into diagnostics payloads', () => {
    const signal: SignalEnhanceSceneStatus = {
      atUtc: '2026-06-15T01:00:00Z',
      profileId: 'dx',
      baseProfileId: 'voice',
      reason: 'sparse coherent weak-signal scene',
      peakCount: 4,
      coherentPeakCount: 3,
      peaksPer10Khz: 0.8,
      occupiedPct: 3.4,
      coherentOccupiedPct: 2.2,
      impulsivePct: 0.5,
      maxSnrDb: 14.2,
      coherentMaxSnrDb: 13.7,
    };
    const smart: SmartNrStatus = {
      atUtc: '2026-06-15T01:00:00Z',
      profile: 'NR2',
      reason: 'weak SSB noise floor',
      heldByRxChain: true,
      rxChainLabel: 'ADC headroom limited',
      rxChainRecommendation: 'Hold headroom; use Smart NR/filtering',
      rxChainTone: 'protect',
      rxChainScore: 62,
      maxSnrDb: 12.8,
      occupancyPct: 5.1,
      coherentOccupancyPct: 2.6,
      impulsivePct: 0.8,
      peakCount: 2,
      coherentPeakCount: 1,
      pending: false,
      applied: true,
      nr: NR_CONFIG_DEFAULT,
    };

    const payload = buildFrontendDspSceneDiagnosticsPayload('USB', signal, smart);

    expect(payload).toMatchObject({
      mode: 'USB',
      signalProfile: 'dx',
      signalReason: 'sparse coherent weak-signal scene',
      smartNrProfile: 'NR2',
      smartNrReason: 'weak SSB noise floor',
      smartNrRecommendation: 'Hold headroom; use Smart NR/filtering',
      smartNrHeldByRxChain: true,
      smartNrRxChainLabel: 'ADC headroom limited',
      maxSnrDb: 12.8,
      coherentMaxSnrDb: 13.7,
      occupiedPct: 5.1,
      coherentOccupiedPct: 2.6,
      impulsivePct: 0.8,
      peakCount: 2,
      coherentPeakCount: 1,
    });
    expect(payload?.sourceClientId).toMatch(/^frontend-/);
  });

  it('does not publish when no frontend scene evidence exists', () => {
    expect(buildFrontendDspSceneDiagnosticsPayload('USB', null, null)).toBeNull();
  });
});
