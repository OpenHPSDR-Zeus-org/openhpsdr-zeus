// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import {
  CFC_CONFIG_DEFAULT,
  TX_LEVELING_CONFIG_DEFAULT,
  TX_PHASE_ROTATOR_CONFIG_DEFAULT,
  type CfcConfigDto,
  type TxLevelingConfigDto,
} from '../api/client';
import {
  recommendTxAutoTune,
  scoreTxPhaseRotatorCandidate,
  summarizeTxAutoTuneSamples,
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

function leveling(overrides: Partial<TxLevelingConfigDto> = {}): TxLevelingConfigDto {
  return { ...TX_LEVELING_CONFIG_DEFAULT, ...overrides };
}

function settings(overrides: Partial<TxAutoTuneSettings> = {}): TxAutoTuneSettings {
  return {
    micGainDb: -6,
    levelerMaxGainDb: 6,
    drivePercent: 80,
    cfcConfig: cfc(),
    txLeveling: leveling(),
    txPhaseRotator: { ...TX_PHASE_ROTATOR_CONFIG_DEFAULT },
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
    compPkDbfs: -8,
    compAvDbfs: -20,
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

  it('eases ALC make-up gain when the ALC limiter is slamming', () => {
    const plan = recommendTxAutoTune(
      settings({ txLeveling: leveling({ alcMaxGainDb: 3 }) }),
      samples({ alcGrDb: 12 }),
    );

    expect(plan.settings.txLeveling.alcMaxGainDb).toBe(2);
    expect(plan.actions.join(' ')).toContain('ALC max gain -1');
    expect(plan.changed).toBe(true);
  });

  it('backs the CPDR compressor off when it pinches the crest', () => {
    const plan = recommendTxAutoTune(
      settings({ txLeveling: leveling({ compressorEnabled: true, compressorGainDb: 6 }) }),
      samples({
        outPkDbfs: -7,
        outAvDbfs: -10, // pinched chain crest -> protecting, no re-raise
        compPkDbfs: -8,
        compAvDbfs: -12, // pinched compressor crest
      }),
    );

    expect(plan.settings.txLeveling.compressorGainDb).toBe(4);
    expect(plan.settings.txLeveling.compressorEnabled).toBe(true);
    expect(plan.actions.join(' ')).toContain('compressor -2');
  });

  it('engages ALC make-up and the compressor on a clean keyed max-density sample', () => {
    const plan = recommendTxAutoTune(
      settings({
        keyed: true,
        drivePercent: 90,
        targetSpectralDensity: 100,
        txLeveling: leveling({ alcMaxGainDb: 3, compressorEnabled: false, compressorGainDb: 0 }),
      }),
      samples({
        micPkDbfs: -14,
        outPkDbfs: -6,
        outAvDbfs: -18, // wide-open 12 dB crest with output headroom
        alcGrDb: 2,
        lvlrGrDb: 2,
        cfcGrDb: 1,
        psFeedbackLevel: 150,
        swr: 1.2,
      }),
    );

    expect(plan.blockers).toEqual([]);
    // Lock the full per-run blast radius of a clean max-density run so the
    // number of dynamics stages that move at once stays explicit.
    expect(plan.settings.txLeveling.alcMaxGainDb).toBe(4); // 3 -> +1
    expect(plan.settings.txLeveling.compressorEnabled).toBe(true);
    expect(plan.settings.txLeveling.compressorGainDb).toBe(2); // off -> on +2
    expect(plan.settings.txLeveling.levelerDecayMs).toBe(140); // 100 -> +40
    expect(plan.settings.cfcConfig.prePeqDb).toBeGreaterThan(0);
    expect(plan.actions.join(' ')).toContain('compressor on');
  });

  it('reports the resulting values so consecutive runs are distinguishable', () => {
    const hot = samples({ micPkDbfs: -8, audioSuiteOutputDbfs: -0.4, outPkDbfs: -6 });
    const first = recommendTxAutoTune(settings({ micGainDb: -4 }), hot);

    // The headline now leads with where each moved setting LANDED, not just the
    // delta — so it carries the absolute mic value and differs run-to-run.
    expect(first.summary).toContain('→');
    expect(first.summary).toContain(`mic ${first.settings.micGainDb} dB`);

    // Re-run from the just-applied mic gain against the same hot sample: the
    // chain backs off again, lands on a different value, and the summary text
    // is no longer identical to the previous run.
    const second = recommendTxAutoTune(settings({ micGainDb: first.settings.micGainDb }), hot);
    expect(second.settings.micGainDb).toBeLessThan(first.settings.micGainDb);
    expect(second.summary).not.toBe(first.summary);
  });

  it('only names settings that actually moved in the result clause', () => {
    const plan = recommendTxAutoTune(
      settings({ micGainDb: -4, audioSuiteActive: true }),
      samples({ micPkDbfs: -8, audioSuiteOutputDbfs: -0.4, outPkDbfs: -6 }),
    );

    // VST chain hot -> only mic gain is cut; the clause must not invent a
    // leveler/drive change the run never made.
    expect(plan.summary).toContain('mic');
    expect(plan.summary).not.toContain('leveler');
    expect(plan.summary).not.toContain('drive');
  });

  it('slows a pumping leveler instead of leaving it chasing syllables', () => {
    const pumping = Array.from({ length: 40 }, (_, i) =>
      sample({ lvlrGrDb: i < 30 ? 2 : 12 }),
    );
    const plan = recommendTxAutoTune(
      settings({ txLeveling: leveling({ levelerDecayMs: 50 }) }),
      pumping,
    );

    expect(plan.settings.txLeveling.levelerDecayMs).toBe(90);
    expect(plan.actions.join(' ')).toContain('leveler decay slower');
  });

  it('does not hardcode phase rotator to the Thetis default in the static plan', () => {
    const currentPhase = { enabled: true, cornerHz: 512, stages: 6, reverse: true };
    const plan = recommendTxAutoTune(
      settings({ txPhaseRotator: currentPhase }),
      samples({
        micPkDbfs: -14,
        outPkDbfs: -7,
        outAvDbfs: -18,
        alcGrDb: 1,
        lvlrGrDb: 1,
        cfcGrDb: 1,
      }),
    );

    expect(plan.settings.txPhaseRotator).toEqual(currentPhase);
    expect(plan.actions.join(' ')).not.toContain('338');
  });

  it('scores phase rotator candidates from measured TX meter quality', () => {
    const good = summarizeTxAutoTuneSamples(samples({
      outPkDbfs: -7,
      outAvDbfs: -15,
      alcGrDb: 1,
      lvlrGrDb: 1,
      cfcGrDb: 1,
    }));
    const hot = summarizeTxAutoTuneSamples(samples({
      outPkDbfs: -0.2,
      outAvDbfs: -6,
      alcGrDb: 9,
      lvlrGrDb: 8,
      cfcGrDb: 6,
    }));

    expect(scoreTxPhaseRotatorCandidate(good, 95).score)
      .toBeGreaterThan(scoreTxPhaseRotatorCandidate(hot, 95).score);
  });
});
