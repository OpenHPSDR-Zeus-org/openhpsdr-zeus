// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License v2 or later. See the
// LICENSE file at the repository root, or https://www.gnu.org/licenses/.

import { afterEach, describe, expect, it } from 'vitest';
import {
  computeSnapToLineHz,
  computeSnapTuneHz,
  detectPeaks,
  enhanceInto,
  findNearestPeakHz,
  findPeakHz,
  getNoiseFloor,
  getSignalConfidence,
  getSnapHistorySpectrum,
  maybeUpdateEstimator,
  measureOccupiedBandwidth,
  measureSignalExtent,
  measureSnapLock,
  peakAlpha,
  resetEstimator,
  SIGNAL_ENHANCE_PROFILES,
  recommendSignalEnhanceScene,
  signalExtentHz,
  signalEnhanceProfileForMode,
  useSignalEnhanceStore,
} from './signal-estimator';

const WIDTH = 256;
const HZ_PER_PX = 100;
const CENTER = 14_200_000;
const NOISE_DB = -110;
const CARRIER_BIN = 150;
const CARRIER_DB = -50;

// Flat noise floor with a single one-bin carrier spike at CARRIER_BIN.
function spectrumWithCarrier(): Float32Array {
  const spec = new Float32Array(WIDTH).fill(NOISE_DB);
  spec[CARRIER_BIN] = CARRIER_DB;
  return spec;
}

// Sloped (band-tilt) noise floor: -90 at the left edge down to -120 at the
// right, plus the same carrier spike.
function spectrumWithTilt(): Float32Array {
  const spec = new Float32Array(WIDTH);
  for (let i = 0; i < WIDTH; i++) spec[i] = -90 + (-30 * i) / WIDTH;
  spec[CARRIER_BIN] = CARRIER_DB;
  return spec;
}

function pushFrame(spec: Float32Array): void {
  maybeUpdateEstimator({ panDb: spec, panValid: true, width: WIDTH, hzPerPixel: HZ_PER_PX });
}

afterEach(() => {
  resetEstimator();
  useSignalEnhanceStore.setState({
    popEnabled: false,
    snapEnabled: false,
    autoNotchEnabled: false,
    visualAgcEnabled: true,
    impulseRejectEnabled: true,
  });
  useSignalEnhanceStore.getState().resetSignalEnhanceTuning();
});

describe('signal estimator — spatial floor', () => {
  it('applies operator profiles and clamps custom signal-intelligence tuning', () => {
    const store = useSignalEnhanceStore.getState();

    store.applySignalEnhanceProfile('dx');
    expect(useSignalEnhanceStore.getState().profileId).toBe('dx');
    expect(useSignalEnhanceStore.getState().popSpanDb).toBe(SIGNAL_ENHANCE_PROFILES.dx.popSpanDb);

    store.setSignalEnhanceTuning({
      popFloorDb: -10,
      popGamma: 2,
      popRenderIntensity: 250,
      snapRadiusHz: 99_999,
    });
    const tuned = useSignalEnhanceStore.getState();
    expect(tuned.profileId).toBe('custom');
    expect(tuned.popFloorDb).toBe(0);
    expect(tuned.popGamma).toBe(1.2);
    expect(tuned.popRenderIntensity).toBe(100);
    expect(tuned.snapRadiusHz).toBe(12_000);
  });

  it('maps RX modes to signal-intelligence profiles and lets manual tuning leave auto mode', () => {
    expect(signalEnhanceProfileForMode('CWU')).toBe('cw');
    expect(signalEnhanceProfileForMode('CWL')).toBe('cw');
    expect(signalEnhanceProfileForMode('DIGU')).toBe('digital');
    expect(signalEnhanceProfileForMode('DIGL')).toBe('digital');
    expect(signalEnhanceProfileForMode('USB')).toBe('voice');
    expect(signalEnhanceProfileForMode('AM')).toBe('voice');

    const store = useSignalEnhanceStore.getState();
    store.setSignalEnhanceAutoProfile(true, 'DIGU');
    expect(useSignalEnhanceStore.getState().autoProfileEnabled).toBe(true);
    expect(useSignalEnhanceStore.getState().profileId).toBe('digital');

    useSignalEnhanceStore.getState().applySignalEnhanceModeProfile('CWU');
    expect(useSignalEnhanceStore.getState().autoProfileEnabled).toBe(true);
    expect(useSignalEnhanceStore.getState().profileId).toBe('cw');

    useSignalEnhanceStore.getState().applySignalEnhanceProfile('dx');
    expect(useSignalEnhanceStore.getState().autoProfileEnabled).toBe(false);
    expect(useSignalEnhanceStore.getState().profileId).toBe('dx');
  });

  it('recommends DX for sparse weak voice-like signals and Contest for crowded spans', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const sparseWeak = new Float32Array(WIDTH).fill(NOISE_DB);
    sparseWeak[140] = NOISE_DB + 15;
    expect(recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: sparseWeak,
      floor,
      hzPerPixel: HZ_PER_PX,
    }).profileId).toBe('dx');

    const strongVoice = new Float32Array(WIDTH).fill(NOISE_DB);
    strongVoice[140] = NOISE_DB + 38;
    expect(recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: strongVoice,
      floor,
      hzPerPixel: HZ_PER_PX,
    }).profileId).toBe('voice');

    const crowded = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 20; i < WIDTH - 20; i += 8) crowded[i] = NOISE_DB + 30;
    const busyScene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: crowded,
      floor,
      hzPerPixel: HZ_PER_PX,
    });
    expect(busyScene.profileId).toBe('contest');
    expect(busyScene.peaksPer10Khz).toBeGreaterThan(3.5);
  });

  it('uses temporal confidence to reject impulsive scene changes', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const lowConfidence = new Float32Array(WIDTH);

    const sparseImpulse = new Float32Array(WIDTH).fill(NOISE_DB);
    sparseImpulse[140] = NOISE_DB + 15;
    const impulseScene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: sparseImpulse,
      floor,
      confidence: lowConfidence,
      hzPerPixel: HZ_PER_PX,
    });
    expect(impulseScene.profileId).toBe('voice');
    expect(impulseScene.peakCount).toBe(1);
    expect(impulseScene.coherentPeakCount).toBe(0);
    expect(impulseScene.impulsiveOccupiedRatio).toBeGreaterThan(0);

    const sparseCoherent = new Float32Array(WIDTH).fill(NOISE_DB);
    const coherentConfidence = new Float32Array(WIDTH);
    sparseCoherent[140] = NOISE_DB + 15;
    coherentConfidence[140] = 0.8;
    const dxScene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: sparseCoherent,
      floor,
      confidence: coherentConfidence,
      hzPerPixel: HZ_PER_PX,
    });
    expect(dxScene.profileId).toBe('dx');
    expect(dxScene.coherentPeakCount).toBe(1);
    expect(dxScene.coherentMaxSnrDb).toBeCloseTo(15, 5);
  });

  it('requires coherent crowding before selecting the Contest profile', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const crowded = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 20; i < WIDTH - 20; i += 8) crowded[i] = NOISE_DB + 30;

    const nonCoherentScene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: crowded,
      floor,
      confidence: new Float32Array(WIDTH),
      hzPerPixel: HZ_PER_PX,
    });
    expect(nonCoherentScene.profileId).toBe('voice');
    expect(nonCoherentScene.peakCount).toBeGreaterThan(10);
    expect(nonCoherentScene.coherentPeakCount).toBe(0);

    const coherentConfidence = new Float32Array(WIDTH);
    for (let i = 20; i < WIDTH - 20; i += 8) coherentConfidence[i] = 0.8;
    const coherentScene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: crowded,
      floor,
      confidence: coherentConfidence,
      hzPerPixel: HZ_PER_PX,
    });
    expect(coherentScene.profileId).toBe('contest');
    expect(coherentScene.coherentPeaksPer10Khz).toBeGreaterThan(3.5);
  });

  it('keeps mode-specific CW and digital profiles even on busy scenes', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const crowded = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 20; i < WIDTH - 20; i += 8) crowded[i] = NOISE_DB + 30;

    expect(recommendSignalEnhanceScene({
      mode: 'CWU',
      spectrum: crowded,
      floor,
      hzPerPixel: HZ_PER_PX,
    }).profileId).toBe('cw');
    expect(recommendSignalEnhanceScene({
      mode: 'DIGU',
      spectrum: crowded,
      floor,
      hzPerPixel: HZ_PER_PX,
    }).profileId).toBe('digital');
  });

  it('ignores non-finite bins when recommending scene profiles', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    const confidence = new Float32Array(WIDTH).fill(0.8);
    spec[80] = Infinity;
    spec[90] = NaN;
    spec[100] = -Infinity;
    floor[110] = NaN;
    confidence[80] = Infinity;

    const scene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: spec,
      floor,
      confidence,
      hzPerPixel: HZ_PER_PX,
    });

    expect(scene.profileId).toBe('voice');
    expect(scene.peakCount).toBe(0);
    expect(scene.coherentPeakCount).toBe(0);
    expect(scene.maxSnrDb).toBe(0);
    expect(scene.coherentMaxSnrDb).toBe(0);
    expect(scene.impulsiveOccupiedRatio).toBe(0);
  });

  it('fails closed on invalid scene geometry without emitting NaN metrics', () => {
    const floor = new Float32Array(WIDTH).fill(NOISE_DB);
    const spec = spectrumWithCarrier();

    for (const hzPerPixel of [Number.NaN, Infinity, 0, -1]) {
      const scene = recommendSignalEnhanceScene({
        mode: 'USB',
        spectrum: spec,
        floor,
        hzPerPixel,
      });

      expect(scene.profileId).toBe('voice');
      expect(scene.peakCount).toBe(0);
      expect(scene.peaksPer10Khz).toBe(0);
      expect(scene.occupiedRatio).toBe(0);
      expect(scene.maxSnrDb).toBe(0);
      expect(Number.isFinite(scene.peaksPer10Khz)).toBe(true);
      expect(Number.isFinite(scene.coherentPeaksPer10Khz)).toBe(true);
    }
  });

  it('stays cold (no floor, zero cost) when both Pop and Snap are off', () => {
    pushFrame(spectrumWithCarrier());
    expect(getNoiseFloor()).toBeNull();
  });

  it('keeps the floor estimator live while auto-profile analysis is enabled', () => {
    useSignalEnhanceStore.setState({ autoProfileEnabled: true, popEnabled: false, snapEnabled: false });
    pushFrame(spectrumWithCarrier());
    expect(getNoiseFloor()).not.toBeNull();
  });

  it('does not warm the estimator from invalid frame geometry', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    maybeUpdateEstimator({
      panDb: spectrumWithCarrier(),
      panValid: true,
      width: WIDTH + 1,
      hzPerPixel: HZ_PER_PX,
    });
    expect(getNoiseFloor()).toBeNull();

    maybeUpdateEstimator({
      panDb: spectrumWithCarrier(),
      panValid: true,
      width: WIDTH,
      hzPerPixel: Number.NaN,
    });
    expect(getNoiseFloor()).toBeNull();
  });

  it('estimates the floor from neighbours — a steady carrier does NOT raise its own floor', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    // Many identical frames: a temporal tracker would creep the carrier bin's
    // floor up to the carrier. The spatial estimate must keep it at the noise.
    for (let k = 0; k < 30; k++) pushFrame(spectrumWithCarrier());
    const floor = getNoiseFloor()!;
    expect(floor).not.toBeNull();
    // Floor at the carrier bin tracks the surrounding noise (+ a few dB offset),
    // nowhere near the −50 dB carrier.
    expect(floor[CARRIER_BIN]!).toBeLessThan(NOISE_DB + 12);
    expect(floor[CARRIER_BIN]!).toBeGreaterThan(NOISE_DB - 2);
  });

  it('maps the carrier near full brightness and the noise to black (0..1)', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    const out = new Float32Array(WIDTH);
    enhanceInto(spec, out);
    // Output is a 0..1 display value: the strong carrier clips to ~1, gated
    // noise sits at 0.
    expect(out[CARRIER_BIN]!).toBeGreaterThan(0.9);
    expect(out[CARRIER_BIN]!).toBeLessThanOrEqual(1);
    expect(out[0]!).toBe(0);
  });

  it('gamma lifts a weak signal well clear of the gate', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    // ~15 dB-over-noise signal: with span 30 + gamma 0.5 it should read as a
    // bright mid value, not buried — this is the "pull weak out" behaviour.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const out = new Float32Array(WIDTH);
    enhanceInto(spec, out);
    expect(out[150]!).toBeGreaterThan(0.45);
    expect(out[0]!).toBe(0);
  });

  it('visual AGC gives sparse weak signals more of the colour ramp', () => {
    useSignalEnhanceStore.setState({
      popEnabled: true,
      visualAgcEnabled: false,
      visualAgcStrength: 0,
    });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[150] = NOISE_DB + 10;
    for (let k = 0; k < 5; k++) pushFrame(spec);

    const fixed = new Float32Array(WIDTH);
    enhanceInto(spec, fixed);

    useSignalEnhanceStore.setState({
      visualAgcEnabled: true,
      visualAgcStrength: 100,
    });
    const agc = new Float32Array(WIDTH);
    enhanceInto(spec, agc);

    expect(agc[150]!).toBeGreaterThan(fixed[150]! + 0.15);
    expect(agc[0]!).toBe(0);
  });

  it('builds temporal confidence for a persistent weak signal', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(spec);

    const confidence = getSignalConfidence()!;
    expect(confidence[150]!).toBeGreaterThan(0.5);
    expect(confidence[0]!).toBeLessThan(0.05);
  });

  it('builds temporal confidence for automation consumers when Signal Pop is off', () => {
    useSignalEnhanceStore.setState({ popEnabled: false, autoProfileEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(spec);

    const confidence = getSignalConfidence()!;
    expect(confidence).not.toBeNull();
    expect(confidence[150]!).toBeGreaterThan(0.5);
    expect(confidence[0]!).toBeLessThan(0.05);
  });

  it('rejects one-frame auto-profile impulses even when Signal Pop is off', () => {
    useSignalEnhanceStore.setState({ popEnabled: false, autoProfileEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);

    const impulse = new Float32Array(WIDTH).fill(NOISE_DB);
    impulse[150] = NOISE_DB + 15;
    pushFrame(impulse);

    const confidence = getSignalConfidence()!;
    const scene = recommendSignalEnhanceScene({
      mode: 'USB',
      spectrum: impulse,
      floor: getNoiseFloor(),
      confidence,
      hzPerPixel: HZ_PER_PX,
    });

    expect(confidence[150]!).toBeLessThan(useSignalEnhanceStore.getState().coherenceHoldGate);
    expect(scene.peakCount).toBe(1);
    expect(scene.coherentPeakCount).toBe(0);
    expect(scene.impulsiveOccupiedRatio).toBeGreaterThan(0);
    expect(scene.profileId).toBe('voice');
  });

  it('does not give a one-frame isolated impulse a persistent trail', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);

    const impulse = new Float32Array(WIDTH).fill(NOISE_DB);
    impulse[150] = NOISE_DB + 45;
    pushFrame(impulse);
    pushFrame(noise);

    const out = new Float32Array(WIDTH);
    enhanceInto(noise, out);
    expect(out[150]!).toBe(0);
  });

  it('display-clamps isolated one-frame spikes but preserves coherent signals', () => {
    useSignalEnhanceStore.setState({
      popEnabled: true,
      visualAgcEnabled: false,
      impulseRejectEnabled: true,
      impulseRejectDb: 18,
    });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);

    const impulse = new Float32Array(WIDTH).fill(NOISE_DB);
    impulse[150] = NOISE_DB + 45;
    pushFrame(impulse);
    const impulseOut = new Float32Array(WIDTH);
    enhanceInto(impulse, impulseOut);

    expect(impulseOut[150]!).toBeLessThan(0.55);

    resetEstimator();
    const coherent = new Float32Array(WIDTH).fill(NOISE_DB);
    coherent[150] = NOISE_DB + 45;
    for (let k = 0; k < 5; k++) pushFrame(coherent);
    const coherentOut = new Float32Array(WIDTH);
    enhanceInto(coherent, coherentOut);

    expect(coherentOut[150]!).toBeGreaterThan(0.9);
  });

  it('boosts a narrow weak ridge above a broad raised neighbourhood at the same SNR', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const narrow = new Float32Array(WIDTH).fill(NOISE_DB);
    narrow[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(narrow);
    const narrowOut = new Float32Array(WIDTH);
    enhanceInto(narrow, narrowOut);

    resetEstimator();

    const broad = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 140; i <= 160; i++) broad[i] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(broad);
    const broadOut = new Float32Array(WIDTH);
    enhanceInto(broad, broadOut);

    expect(narrowOut[150]!).toBeGreaterThan(broadOut[150]! + 0.08);
  });

  it('flattens band tilt — gated noise is uniformly dark across the span', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithTilt());
    const spec = spectrumWithTilt();
    const out = new Float32Array(WIDTH);
    enhanceInto(spec, out);
    // Left edge (−90) and right edge (−120) differ by 30 dB raw; after floor
    // subtraction + gate both are noise and land at 0.
    expect(out[5]!).toBe(0);
    expect(out[WIDTH - 5]!).toBe(0);
  });

  it('does not let one deep spectral null light nearby noise', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[80] = NOISE_DB - 40;
    spec[90] = NOISE_DB;
    spec[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(spec);

    const out = new Float32Array(WIDTH);
    enhanceInto(spec, out);

    expect(out[90]!).toBe(0);
    expect(out[150]!).toBeGreaterThan(0.45);
  });

  it('holds a weak signal ridge briefly, then decays back to black', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const weak = new Float32Array(WIDTH).fill(NOISE_DB);
    weak[150] = NOISE_DB + 15;
    for (let k = 0; k < 5; k++) pushFrame(weak);

    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    pushFrame(noise);
    const held = new Float32Array(WIDTH);
    enhanceInto(noise, held);

    expect(held[150]!).toBeGreaterThan(0.35);
    expect(held[0]!).toBe(0);

    for (let k = 0; k < 20; k++) pushFrame(noise);
    const faded = new Float32Array(WIDTH);
    enhanceInto(noise, faded);

    expect(faded[150]!).toBe(0);
  });

  it('enhanceInto outputs all-zero before any floor exists', () => {
    const spec = spectrumWithCarrier();
    const out = new Float32Array(WIDTH).fill(0.5);
    enhanceInto(spec, out); // no floor yet
    expect(Array.from(out).every((v) => v === 0)).toBe(true);
  });

  it('keeps non-finite estimator samples dark and out of confidence memory', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);

    const bad = new Float32Array(WIDTH).fill(NOISE_DB);
    bad[149] = Infinity;
    bad[150] = NaN;
    bad[151] = -Infinity;
    pushFrame(bad);

    const out = new Float32Array(WIDTH).fill(0.5);
    enhanceInto(bad, out);
    const confidence = getSignalConfidence()!;

    expect(Array.from(out).every(Number.isFinite)).toBe(true);
    expect(out[149]).toBe(0);
    expect(out[150]).toBe(0);
    expect(out[151]).toBe(0);
    expect(confidence[149]).toBe(0);
    expect(confidence[150]).toBe(0);
    expect(confidence[151]).toBe(0);
  });
});

describe('signal estimator — snap-to-signal', () => {
  it('snaps a near-carrier click onto the exact carrier bin frequency', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    // Click 1.8 kHz away from the carrier (carrier is at +2200 Hz from center).
    const clickHz = CENTER + 2000;
    const peak = findPeakHz(spec, CENTER, HZ_PER_PX, clickHz);
    const carrierHz = CENTER + (CARRIER_BIN - WIDTH / 2) * HZ_PER_PX;
    expect(peak).toBe(carrierHz);
  });

  it('returns null over bare noise (nothing rises above the floor)', () => {
    useSignalEnhanceStore.setState({ popEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    // Click far from the carrier, on flat noise.
    const peak = findPeakHz(spec, CENTER, HZ_PER_PX, CENTER - 5000);
    expect(peak).toBeNull();
  });
});

describe('signal estimator — peak markers (CFAR)', () => {
  it('detects the carrier and nothing else on a flat-noise span', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const peaks = detectPeaks(spectrumWithCarrier(), CENTER, HZ_PER_PX);
    expect(peaks).toHaveLength(1);
    expect(peaks[0]!.hz).toBe(CENTER + (CARRIER_BIN - WIDTH / 2) * HZ_PER_PX);
    expect(peaks[0]!.snrDb).toBeGreaterThan(45);
  });

  it('collapses a multi-bin signal to a single strongest marker', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    // A 3-bin-wide signal cresting at the middle bin.
    spec[120] = -70;
    spec[121] = -55;
    spec[122] = -68;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const peaks = detectPeaks(spec, CENTER, HZ_PER_PX);
    expect(peaks).toHaveLength(1);
    expect(peaks[0]!.hz).toBe(CENTER + (121 - WIDTH / 2) * HZ_PER_PX);
  });

  it('uses the configured marker SNR threshold for peak detection', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const weak = new Float32Array(WIDTH).fill(NOISE_DB);
    weak[CARRIER_BIN] = NOISE_DB + 12;
    for (let k = 0; k < 5; k++) pushFrame(weak);

    useSignalEnhanceStore.getState().setSignalEnhanceTuning({ peakMinSnrDb: 4 });
    expect(detectPeaks(weak, CENTER, HZ_PER_PX)).toHaveLength(1);

    useSignalEnhanceStore.getState().setSignalEnhanceTuning({ peakMinSnrDb: 12 });
    expect(detectPeaks(weak, CENTER, HZ_PER_PX)).toHaveLength(0);
  });

  it('returns nothing before a floor exists', () => {
    expect(detectPeaks(spectrumWithCarrier(), CENTER, HZ_PER_PX)).toEqual([]);
  });

  it('does not treat non-finite bins as snap peaks or signal extents', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);

    const bad = new Float32Array(WIDTH).fill(NOISE_DB);
    bad[149] = Infinity;
    bad[150] = NaN;
    bad[151] = -Infinity;
    const clickHz = CENTER + (150 - WIDTH / 2) * HZ_PER_PX;

    expect(detectPeaks(bad, CENTER, HZ_PER_PX)).toEqual([]);
    expect(findPeakHz(bad, CENTER, HZ_PER_PX, clickHz)).toBeNull();
    expect(computeSnapTuneHz(bad, CENTER, HZ_PER_PX, clickHz, 5000, 'USB')).toBeNull();
    expect(computeSnapToLineHz(bad, CENTER, HZ_PER_PX, 'USB', clickHz, 5000)).toBeNull();
    expect(signalExtentHz(bad, CENTER, HZ_PER_PX, clickHz, 5000)).toBeNull();
  });

  it('fails closed when snap and marker geometry is non-finite', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    const clickHz = CENTER + (CARRIER_BIN - WIDTH / 2) * HZ_PER_PX;

    for (const centerHz of [Number.NaN, Infinity]) {
      expect(detectPeaks(spec, centerHz, HZ_PER_PX)).toEqual([]);
      expect(findPeakHz(spec, centerHz, HZ_PER_PX, clickHz)).toBeNull();
      expect(findNearestPeakHz(spec, centerHz, HZ_PER_PX, clickHz, 5000)).toBeNull();
      expect(computeSnapTuneHz(spec, centerHz, HZ_PER_PX, clickHz, 5000, 'USB')).toBeNull();
      expect(computeSnapToLineHz(spec, centerHz, HZ_PER_PX, 'USB', clickHz, 5000)).toBeNull();
      expect(signalExtentHz(spec, centerHz, HZ_PER_PX, clickHz, 5000)).toBeNull();
      expect(measureSnapLock(spec, centerHz, HZ_PER_PX, 'USB', clickHz, 500)).toBeNull();
    }

    for (const hzPerPixel of [Number.NaN, Infinity, 0, -1]) {
      expect(detectPeaks(spec, CENTER, hzPerPixel)).toEqual([]);
      expect(findPeakHz(spec, CENTER, hzPerPixel, clickHz)).toBeNull();
      expect(findNearestPeakHz(spec, CENTER, hzPerPixel, clickHz, 5000)).toBeNull();
      expect(computeSnapTuneHz(spec, CENTER, hzPerPixel, clickHz, 5000, 'USB')).toBeNull();
      expect(computeSnapToLineHz(spec, CENTER, hzPerPixel, 'USB', clickHz, 5000)).toBeNull();
      expect(signalExtentHz(spec, CENTER, hzPerPixel, clickHz, 5000)).toBeNull();
      expect(measureSnapLock(spec, CENTER, hzPerPixel, 'USB', clickHz, 500)).toBeNull();
    }

    expect(findNearestPeakHz(spec, CENTER, HZ_PER_PX, clickHz, Number.NaN)).toBeNull();
    expect(computeSnapTuneHz(spec, CENTER, HZ_PER_PX, clickHz, Infinity, 'USB')).toBeNull();
    expect(computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'USB', clickHz, -1)).toBeNull();
    expect(signalExtentHz(spec, CENTER, HZ_PER_PX, clickHz, Number.NaN)).toBeNull();
    expect(measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', clickHz, -1)).toBeNull();
  });

  it('peakAlpha scales with SNR and stays within [0.45, 1]', () => {
    expect(peakAlpha(0)).toBe(0.45);
    expect(peakAlpha(1000)).toBe(1);
    expect(peakAlpha(NaN)).toBe(0.45);
    expect(peakAlpha(30)).toBeGreaterThan(0.45);
    expect(peakAlpha(30)).toBeLessThan(1);
  });
});

describe('signal estimator — findNearestPeakHz (snap)', () => {
  const carrierHz = CENTER + (CARRIER_BIN - WIDTH / 2) * HZ_PER_PX;

  it('snaps a nearby click onto the carrier within the radius', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    // Click 1.5 kHz off the carrier, radius 5 kHz → snaps.
    const got = findNearestPeakHz(spec, CENTER, HZ_PER_PX, carrierHz - 1500, 5000);
    expect(got).toBe(carrierHz);
  });

  it('returns null when the nearest carrier is outside the radius', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const spec = spectrumWithCarrier();
    // Click 8 kHz off the carrier, radius 3 kHz → no snap.
    const got = findNearestPeakHz(spec, CENTER, HZ_PER_PX, carrierHz - 8000, 3000);
    expect(got).toBeNull();
  });

  it('picks the closer of two carriers', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    spec[110] = -55;
    spec[150] = -55;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const hz110 = CENTER + (110 - WIDTH / 2) * HZ_PER_PX;
    const hz150 = CENTER + (150 - WIDTH / 2) * HZ_PER_PX;
    // Click just left of bin 150 → should grab 150, not 110.
    const got = findNearestPeakHz(spec, CENTER, HZ_PER_PX, hz150 - 500, 1_000_000);
    expect(got).toBe(hz150);
    expect(got).not.toBe(hz110);
  });
});

describe('signal estimator — computeSnapTuneHz (mode-aware tune)', () => {
  const carrierHz = CENTER + (CARRIER_BIN - WIDTH / 2) * HZ_PER_PX; // bin 150
  const binHz = (b: number) => CENTER + (b - WIDTH / 2) * HZ_PER_PX;

  // A ~20-bin voice channel cresting at bin 150 (formant), energy 140..160.
  function voiceBlock(): Float32Array {
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 140; i <= 160; i++) spec[i] = -60 - Math.abs(i - 150);
    return spec;
  }

  it('CW: dials the carrier itself (sub-bin), backend adds the pitch', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const got = computeSnapTuneHz(spectrumWithCarrier(), CENTER, HZ_PER_PX, carrierHz - 800, 5000, 'CWU');
    // Symmetric single-bin carrier → parabolic peak lands exactly on the bin.
    expect(got).toBeCloseTo(carrierHz, 3);
  });

  it('USB: dials the LOW energy edge (suppressed carrier)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapTuneHz(spec, CENTER, HZ_PER_PX, carrierHz, 5000, 'USB');
    expect(got).toBe(binHz(140));
  });

  it('USB: clicking the MIDDLE/RIGHT of a signal still snaps to the LOW edge', () => {
    // The reported bug: click inside a signal (not on its crest) and it stayed
    // put. With anchor-on-strongest-nearby-bin it must still reach the low edge.
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = voiceBlock(); // energy 140..160, crest 150
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Click at bin 156 (right of crest, well inside the signal).
    const got = computeSnapTuneHz(spec, CENTER, HZ_PER_PX, binHz(156), 5000, 'USB');
    expect(got).toBe(binHz(140));
    expect(got!).toBeLessThan(binHz(156)); // moved LEFT, didn't stay put
  });

  it('LSB: dials the HIGH energy edge', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapTuneHz(spec, CENTER, HZ_PER_PX, carrierHz, 5000, 'LSB');
    expect(got).toBe(binHz(160));
  });

  it('AM: dials the carrier centre (energy centroid)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapTuneHz(spec, CENTER, HZ_PER_PX, carrierHz, 5000, 'AM')!;
    // Symmetric hump → centroid within a bin of the centre.
    expect(Math.abs(got - carrierHz)).toBeLessThan(HZ_PER_PX);
  });

  it('returns null when no signal is within the radius', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 5; k++) pushFrame(spectrumWithCarrier());
    const got = computeSnapTuneHz(spectrumWithCarrier(), CENTER, HZ_PER_PX, CENTER - 5000, 1000, 'USB');
    expect(got).toBeNull();
  });
});

describe('signal estimator — computeSnapToLineHz (favour the clicked line)', () => {
  const binHz = (b: number) => CENTER + (b - WIDTH / 2) * HZ_PER_PX;
  const WIDE = 1_000_000; // radius wide enough to reach any on-screen signal

  // Two voice channels: A near the centre (energy 118..128, crest 123),
  // B well to the right (energy 180..200, crest 190). CENTER maps to bin 128.
  function twoBlocks(): Float32Array {
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 118; i <= 128; i++) spec[i] = -60 - Math.abs(i - 123);
    for (let i = 180; i <= 200; i++) spec[i] = -60 - Math.abs(i - 190);
    return spec;
  }

  it('USB: snaps to the LOW edge of the signal nearest the clicked line', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = twoBlocks();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Click on A → A's low edge (118), which is far closer than B's edge (180).
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'USB', binHz(123), WIDE);
    expect(got).toBe(binHz(118));
  });

  it('honours WHERE you click — a click by the far signal snaps to IT, not the one near centre', () => {
    // Regression for the "always snaps to centre" bug: clicking next to the
    // far-right signal must grab IT, even though A sits closer to the display
    // centre. The reference line is the cursor, not the display centre.
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = twoBlocks();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Click on B (crest 190) → USB low edge of B is 180; A is left untouched.
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'USB', binHz(190), WIDE);
    expect(got).toBe(binHz(180));
  });

  it('picks the edge closest to the clicked line, not the nearest signal body', () => {
    // A spans 100..118 (USB low edge 100, FAR from the click at bin 128);
    // B spans 135..160 (low edge 135, only 7 bins from the click). USB must dial
    // B's low edge — that EDGE is closest to the clicked line. The 16-bin gap
    // keeps them as distinct clusters (walkEdge merges ≤3-bin gaps).
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 100; i <= 118; i++) spec[i] = -60 - Math.abs(i - 109);
    for (let i = 135; i <= 160; i++) spec[i] = -60 - Math.abs(i - 147);
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'USB', binHz(128), WIDE);
    expect(got).toBe(binHz(135));
  });

  it('CW: snaps to the carrier crest nearest the clicked line', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = twoBlocks();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'CWU', binHz(128), WIDE)!;
    // A's crest (bin 123) is the nearest crest to the clicked line (bin 128).
    expect(Math.abs(got - binHz(123))).toBeLessThan(HZ_PER_PX);
  });

  it('LSB: clicking the LOW side of a wide channel still snaps to its HIGH edge', () => {
    // The live-G2 case: a ~3 kHz LSB channel (energy 120..150, 30 bins). Click
    // the low side (bin 122); the tuning HIGH edge (bin 150) is 2.8 kHz away —
    // beyond a tight 1.5 kHz grab radius. Body-distance selection still grabs the
    // signal (the click is INSIDE it) and dials the high edge. An edge-distance
    // rule would have missed the very signal under the cursor.
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 120; i <= 150; i++) spec[i] = -60 - Math.abs(i - 135) * 0.3;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'LSB', binHz(122), 1500);
    expect(got).toBe(binHz(150));
  });

  it('returns null when no signal is within the snap radius (click tunes normally)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = twoBlocks();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Click at bin 60, far from both signals; a tight 1 kHz radius → no snap.
    const got = computeSnapToLineHz(spec, CENTER, HZ_PER_PX, 'USB', binHz(60), 1000);
    expect(got).toBeNull();
  });

  it('returns null on bare noise (caller then tunes normally)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 5; k++) pushFrame(noise);
    expect(computeSnapToLineHz(noise, CENTER, HZ_PER_PX, 'USB', CENTER, WIDE)).toBeNull();
  });
});

describe('signal estimator — snap history (waterfall memory)', () => {
  const binHz = (b: number) => CENTER + (b - WIDTH / 2) * HZ_PER_PX;
  const WIDE = 1_000_000;

  // A voice channel, energy 140..160, crest 150.
  function voiceBlock(): Float32Array {
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 140; i <= 160; i++) spec[i] = -60 - Math.abs(i - 150);
    return spec;
  }

  it('only accumulates while Snap is on (Pop alone leaves it empty)', () => {
    useSignalEnhanceStore.setState({ popEnabled: true, snapEnabled: false });
    for (let k = 0; k < 6; k++) pushFrame(voiceBlock());
    expect(getSnapHistorySpectrum()).toBeNull();
  });

  it('never remembers bare noise (no false history clusters)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let k = 0; k < 10; k++) pushFrame(noise);
    expect(getSnapHistorySpectrum()).toBeNull();
  });

  it('remembers a signal that has left the live frame, then snaps to it', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 6; k++) pushFrame(voiceBlock()); // build the memory
    // The signal is gone now — one live noise frame keeps the floor sane.
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    pushFrame(noise);
    // Live snap finds nothing where the signal used to be...
    expect(computeSnapToLineHz(noise, CENTER, HZ_PER_PX, 'USB', binHz(150), WIDE)).toBeNull();
    // ...but the waterfall memory still has it, edge-aligned for the mode.
    const history = getSnapHistorySpectrum();
    expect(history).not.toBeNull();
    const got = computeSnapToLineHz(history!, CENTER, HZ_PER_PX, 'USB', binHz(150), WIDE);
    expect(got).toBe(binHz(140)); // USB low edge of the remembered channel
  });

  it('a remembered signal fades below the snap threshold after enough quiet frames', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    for (let k = 0; k < 6; k++) pushFrame(voiceBlock());
    const noise = new Float32Array(WIDTH).fill(NOISE_DB);
    // Crest SNR starts ~50 dB; at 0.4 dB/frame it needs >100 quiet frames to
    // fade under the 6 dB cluster gate. 200 is comfortably past that.
    for (let k = 0; k < 200; k++) pushFrame(noise);
    const history = getSnapHistorySpectrum();
    if (history) {
      expect(computeSnapToLineHz(history, CENTER, HZ_PER_PX, 'USB', binHz(150), WIDE)).toBeNull();
    } else {
      expect(history).toBeNull();
    }
  });
});

describe('signal estimator — measureSnapLock (self-correcting lock, neighbour-safe)', () => {
  const binHz = (b: number) => CENTER + (b - WIDTH / 2) * HZ_PER_PX;
  const CAPTURE_HZ = 300; // ±3 bins at 100 Hz/px

  // Weak channel (120..130, crest 125) sitting near a MUCH louder neighbour
  // (145..160, crest 152) — the operator's exact worry.
  function weakNearStrong(): Float32Array {
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 120; i <= 130; i++) spec[i] = -92 - Math.abs(i - 125) * 0.5;
    for (let i = 145; i <= 160; i++) spec[i] = -55 - Math.abs(i - 152) * 0.3;
    return spec;
  }

  it('follows the WEAK signal and never grabs the loud neighbour', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = weakNearStrong();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Anchor on the weak signal; the strong one is ~27 bins away, far outside ±3.
    const m = measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ)!;
    expect(m).not.toBeNull();
    expect(m.dialHz).toBe(binHz(120)); // USB low edge of the WEAK channel
    expect(Math.abs(m.bodyHz - binHz(125))).toBeLessThan(2 * HZ_PER_PX);
    expect(m.dialHz).toBeLessThan(binHz(140)); // nowhere near the neighbour (145+)
  });

  it('HOLDS (returns null) when the locked signal fades — does NOT jump to the neighbour', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    // Only the strong neighbour remains; the weak one we locked is gone.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 145; i <= 160; i++) spec[i] = -55 - Math.abs(i - 152) * 0.3;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // Anchored on the (now-empty) weak slot: nothing in the ±3-bin window.
    expect(measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ)).toBeNull();
  });

  it('ignores a signal outside the capture window', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 138; i <= 146; i++) spec[i] = -60 - Math.abs(i - 142); // ~17 bins from anchor
    for (let k = 0; k < 5; k++) pushFrame(spec);
    expect(measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ)).toBeNull();
  });

  it('tracks the signal as it drifts within the window', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    // Signal shifted +3 bins to 123..133 (crest 128) since the lock anchored at
    // bin 125; the crest is still inside the ±3-bin window.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 123; i <= 133; i++) spec[i] = -90 - Math.abs(i - 128) * 0.5;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const m = measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ)!;
    expect(m).not.toBeNull();
    expect(m.dialHz).toBe(binHz(123)); // followed the drift to the new low edge
  });

  // The close-neighbour hole the capture window alone can't close: a loud signal
  // spaced INSIDE the window once our weak lock dips into the noise. The identity
  // gate (tracked level) is what rejects it.
  function loudNeighbourInWindow(): Float32Array {
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    // Our weak signal is gone; a strong carrier crests at bin 127 — 2 bins from
    // the anchor at 125, well inside the ±3-bin window.
    for (let i = 124; i <= 130; i++) spec[i] = -55 - Math.abs(i - 127) * 2;
    return spec;
  }

  it('without a tracked level, grabs the close loud neighbour (the hole)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = loudNeighbourInWindow();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // No anchorLevelDb supplied ⇒ gate inert ⇒ nearest-peak grabs the neighbour.
    const m = measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ);
    expect(m).not.toBeNull();
    expect(Math.abs(m!.bodyHz - binHz(127))).toBeLessThan(2 * HZ_PER_PX);
  });

  it('identity gate HOLDS when a displaced louder neighbour invades the window', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    const spec = loudNeighbourInWindow();
    for (let k = 0; k < 5; k++) pushFrame(spec);
    // We have been tracking a weak (~-92 dB) signal; the in-window candidate is
    // ~+37 dB louder AND displaced ⇒ a foreign carrier ⇒ hold, don't jump.
    expect(measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ, -92)).toBeNull();
  });

  it('identity gate KEEPS a signal that merely brightens in place (QSB lift)', () => {
    useSignalEnhanceStore.setState({ snapEnabled: true });
    // Our signal at the anchor (crest 125) jumps from ~-92 to -70 dB but does NOT
    // move — that is our lock getting louder, not a neighbour, so it is kept.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 121; i <= 129; i++) spec[i] = -70 - Math.abs(i - 125) * 2;
    for (let k = 0; k < 5; k++) pushFrame(spec);
    const m = measureSnapLock(spec, CENTER, HZ_PER_PX, 'USB', binHz(125), CAPTURE_HZ, -92);
    expect(m).not.toBeNull();
    expect(Math.abs(m!.bodyHz - binHz(125))).toBeLessThan(2 * HZ_PER_PX);
  });
});

describe('measureOccupiedBandwidth', () => {
  it('finds the −6 dB edges of a triangular carrier', () => {
    // Crest at bin 120 (−40 dB), sloping 1 dB/bin → −6 dB points are ±6 bins.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 100; i <= 140; i++) spec[i] = -40 - Math.abs(i - 120);
    const [lo, hi] = measureOccupiedBandwidth(spec, 120, 6, 40);
    expect(lo).toBe(114);
    expect(hi).toBe(126);
  });

  it('stops at the valley between two adjacent carriers', () => {
    // Two crests (bin 110 and 140) with a deep notch between them: the walk from
    // the left carrier must not leak across the valley into the right one.
    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    for (let i = 100; i <= 120; i++) spec[i] = -50 - Math.abs(i - 110);
    for (let i = 130; i <= 150; i++) spec[i] = -50 - Math.abs(i - 140);
    const [, hi] = measureOccupiedBandwidth(spec, 110, 6, 60);
    expect(hi).toBeLessThan(130);
  });

  it('respects the max-radius bound', () => {
    const spec = new Float32Array(WIDTH).fill(-40); // flat-topped, never drops
    const [lo, hi] = measureOccupiedBandwidth(spec, 128, 6, 5);
    expect(lo).toBe(123);
    expect(hi).toBe(133);
  });

  it('keeps non-finite bandwidth and extent measurements bounded', () => {
    const badCrest = new Float32Array(WIDTH).fill(NOISE_DB);
    badCrest[120] = NaN;
    expect(measureOccupiedBandwidth(badCrest, 120, 6, 40)).toEqual([120, 120]);
    expect(measureSignalExtent(badCrest, null, 120, 3, 40)).toEqual([120, 120]);
    expect(measureOccupiedBandwidth(badCrest, NaN, 6, 40)).toEqual([0, 0]);
    expect(measureSignalExtent(badCrest, null, NaN, 3, 40)).toEqual([0, 0]);

    const spec = new Float32Array(WIDTH).fill(NOISE_DB);
    const floor = new Float32Array(WIDTH).fill(NOISE_DB - 5);
    for (let i = 116; i <= 124; i++) spec[i] = -55 - Math.abs(i - 120);
    floor[119] = NaN;

    const [lo, hi] = measureSignalExtent(spec, floor, 120, 3, 40);
    expect(lo).toBe(120);
    expect(hi).toBeGreaterThan(120);
  });
});
