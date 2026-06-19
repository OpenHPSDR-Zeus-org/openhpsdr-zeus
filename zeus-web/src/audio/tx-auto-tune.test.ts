// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import { CFC_CONFIG_DEFAULT, type CfcConfigDto } from '../api/client';
import {
  recommendTxAutoTune,
  type TxAutoTuneSample,
  type TxAutoTuneSettings,
} from './tx-auto-tune';

function cfc(preCompDb = 1): CfcConfigDto {
  return {
    ...CFC_CONFIG_DEFAULT,
    enabled: true,
    postEqEnabled: true,
    preCompDb,
    bands: CFC_CONFIG_DEFAULT.bands.map((band) => ({ ...band })),
  };
}

function settings(overrides: Partial<TxAutoTuneSettings> = {}): TxAutoTuneSettings {
  return {
    micGainDb: -6,
    levelerMaxGainDb: 6,
    drivePercent: 80,
    cfcConfig: cfc(),
    targetSpectralDensity: 95,
    keyed: false,
    audioSuiteActive: true,
    vstActive: true,
    ...overrides,
  };
}

function sample(overrides: Partial<TxAutoTuneSample> = {}): TxAutoTuneSample {
  return {
    micPkDbfs: -14,
    outPkDbfs: -7,
    outAvDbfs: -18,
    audioSuiteOutputDbfs: -7,
    alcGrDb: 3,
    lvlrGrDb: 3,
    cfcGrDb: 2,
    swr: 1.15,
    psFeedbackLevel: 150,
    vstDegradedDelta: 0,
    ingestDroppedFrameDelta: 0,
    txBlockDelta: 1,
    p2QueuedPackets: 0,
    p2TransportFailureDelta: 0,
    p2QueueFailureDelta: 0,
    ...overrides,
  };
}

function samples(overrides: Partial<TxAutoTuneSample> = {}, count = 40): TxAutoTuneSample[] {
  return Array.from({ length: count }, (_, i) =>
    sample({
      micPkDbfs: -14 + (i % 4) * 0.2,
      outPkDbfs: -7 + (i % 5) * 0.15,
      outAvDbfs: -18 + (i % 3) * 0.15,
      ...overrides,
    }),
  );
}

describe('recommendTxAutoTune', () => {
  it('raises clean under-driven speech density in bounded steps', () => {
    const plan = recommendTxAutoTune(
      settings({ micGainDb: -12, levelerMaxGainDb: 4, targetSpectralDensity: 95 }),
      samples({
        micPkDbfs: -24,
        outPkDbfs: -15,
        outAvDbfs: -31,
        alcGrDb: 0.5,
        lvlrGrDb: 0.8,
        cfcGrDb: 0.5,
      }),
    );

    expect(plan.blockers).toEqual([]);
    expect(plan.changed).toBe(true);
    expect(plan.settings.micGainDb).toBeGreaterThan(-12);
    expect(plan.settings.levelerMaxGainDb).toBeGreaterThan(4);
    expect(plan.settings.cfcConfig.preCompDb).toBeGreaterThan(1);
    expect(plan.actions.join(' ')).toContain('mic');
  });

  it('does not raise density while VST or transport health counters move', () => {
    const plan = recommendTxAutoTune(
      settings({ micGainDb: -12, targetSpectralDensity: 100 }),
      samples({
        micPkDbfs: -24,
        outPkDbfs: -15,
        outAvDbfs: -31,
        vstDegradedDelta: 1,
      }),
    );

    expect(plan.blockers.join(' ')).toContain('audio engine');
    expect(plan.settings.micGainDb).toBe(-12);
    expect(plan.settings.levelerMaxGainDb).toBe(6);
    expect(plan.changed).toBe(false);
  });

  it('holds settings when diagnostics do not show fresh TXA blocks', () => {
    const plan = recommendTxAutoTune(
      settings({ micGainDb: -12, levelerMaxGainDb: 4, targetSpectralDensity: 100 }),
      samples({
        micPkDbfs: -24,
        outPkDbfs: -15,
        outAvDbfs: -31,
        alcGrDb: 0.5,
        lvlrGrDb: 0.8,
        cfcGrDb: 0.5,
        txBlockDelta: 0,
      }),
    );

    expect(plan.blockers.join(' ')).toContain('TXA did not process fresh mic blocks');
    expect(plan.summary).toContain('fresh TX monitor samples');
    expect(plan.settings.micGainDb).toBe(-12);
    expect(plan.settings.levelerMaxGainDb).toBe(4);
    expect(plan.changed).toBe(false);
  });

  it('backs down mic gain when the VST chain output is hot', () => {
    const plan = recommendTxAutoTune(
      settings({ micGainDb: -4, audioSuiteActive: true, vstActive: true }),
      samples({
        micPkDbfs: -8,
        audioSuiteOutputDbfs: -0.4,
        outPkDbfs: -6,
      }),
    );

    expect(plan.settings.micGainDb).toBeLessThan(-4);
    expect(plan.actions.join(' ')).toContain('headroom');
    expect(plan.changed).toBe(true);
  });

  it('only raises drive from a clean keyed max-density sample', () => {
    const plan = recommendTxAutoTune(
      settings({ keyed: true, drivePercent: 90, targetSpectralDensity: 100 }),
      samples({
        outPkDbfs: -6,
        outAvDbfs: -16,
        psFeedbackLevel: 150,
        swr: 1.2,
      }),
    );

    expect(plan.settings.drivePercent).toBe(95);
    expect(plan.actions).toContain('drive +5% after clean keyed sample');
  });
});
