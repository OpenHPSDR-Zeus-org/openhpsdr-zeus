// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { SignalEnhanceSceneStatus } from '../dsp/signal-estimator';
import type { SmartNrStatus } from '../state/smart-nr-store';
import { NR_CONFIG_DEFAULT } from '../api/client';
import { buildFrontendDspSceneDiagnosticsPayload } from './dspSceneDiagnosticsPayload';

describe('buildFrontendDspSceneDiagnosticsPayload', () => {
  it('mirrors Signal Intelligence and Smart NR evidence into diagnostics payloads', () => {
    const signal: SignalEnhanceSceneStatus = {
      atUtc: '2026-06-15T01:00:10Z',
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
      coherentSubthresholdSignal: true,
      pending: false,
      applied: true,
      nr: NR_CONFIG_DEFAULT,
    };

    const payload = buildFrontendDspSceneDiagnosticsPayload('USB', signal, smart);

    expect(payload).toMatchObject({
      sourceAtUtc: '2026-06-15T01:00:10Z',
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
      coherentSubthresholdSignal: true,
    });
    expect(payload?.sourceClientId).toMatch(/^frontend-/);
  });

  it('publishes a fresh client heartbeat when no scene evidence exists yet', () => {
    const payload = buildFrontendDspSceneDiagnosticsPayload('USB', null, null);

    expect(payload).toMatchObject({
      sourceAtUtc: null,
      mode: 'USB',
      signalProfile: null,
      signalReason: null,
      smartNrProfile: null,
      smartNrReason: null,
      smartNrRecommendation: null,
      smartNrHeldByRxChain: null,
      smartNrRxChainLabel: null,
      maxSnrDb: null,
      coherentMaxSnrDb: null,
      occupiedPct: null,
      coherentOccupiedPct: null,
      impulsivePct: null,
      peakCount: null,
      coherentPeakCount: null,
      coherentSubthresholdSignal: null,
    });
    expect(payload?.sourceClientId).toMatch(/^frontend-/);
  });

  it('uses the latest valid source analysis timestamp', () => {
    const signal: SignalEnhanceSceneStatus = {
      atUtc: '2026-06-15T01:00:00Z',
      profileId: 'dx',
      baseProfileId: 'voice',
      reason: 'sparse weak signal',
      peakCount: 1,
      coherentPeakCount: 1,
      peaksPer10Khz: 0.4,
      occupiedPct: 1.2,
      coherentOccupiedPct: 0.8,
      impulsivePct: 0,
      maxSnrDb: 7.2,
      coherentMaxSnrDb: 6.9,
    };
    const smart: SmartNrStatus = {
      atUtc: '2026-06-15T01:00:30Z',
      profile: 'NR4',
      reason: 'weak narrow signal',
      maxSnrDb: 7.2,
      occupancyPct: 1.2,
      coherentOccupancyPct: 0.8,
      impulsivePct: 0,
      peakCount: 1,
      coherentPeakCount: 1,
      pending: false,
      applied: true,
      nr: NR_CONFIG_DEFAULT,
    };

    const payload = buildFrontendDspSceneDiagnosticsPayload('USB', signal, smart);

    expect(payload?.sourceAtUtc).toBe('2026-06-15T01:00:30Z');
  });

  it('publishes DSP capability limits as the Smart NR action when RX chain is clear', () => {
    const smart: SmartNrStatus = {
      atUtc: '2026-06-15T01:00:30Z',
      profile: 'NR2',
      reason: 'weak narrow signal',
      capabilityLimited: true,
      capabilityRecommendation: 'NR4/SBNR unavailable; using NR2/EMNR fallback.',
      maxSnrDb: 7.2,
      occupancyPct: 1.2,
      coherentOccupancyPct: 0.8,
      impulsivePct: 0,
      peakCount: 1,
      coherentPeakCount: 1,
      pending: false,
      applied: false,
      nr: NR_CONFIG_DEFAULT,
    };

    const payload = buildFrontendDspSceneDiagnosticsPayload('CWU', null, smart);

    expect(payload?.smartNrProfile).toBe('NR2');
    expect(payload?.smartNrRecommendation).toBe('NR4/SBNR unavailable; using NR2/EMNR fallback.');
  });
});
