// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { createMicUplinkAutoGain } from './mic-uplink-gain';

function peak(samples: Float32Array): number {
  return samples.reduce((max, v) => Math.max(max, Math.abs(v)), 0);
}

describe('mic uplink auto gain', () => {
  it('raises very quiet mobile mic blocks toward usable TX level', () => {
    const gain = createMicUplinkAutoGain();
    const samples = new Float32Array(960).fill(0.001);

    const first = gain.process(samples, 0.001);
    const second = gain.process(samples, 0.001);

    expect(first.gain).toBeGreaterThan(1);
    expect(second.gain).toBeGreaterThan(first.gain);
    expect(peak(second.samples)).toBeGreaterThan(0.08);
  });

  it('leaves normal-level blocks at unity gain', () => {
    const gain = createMicUplinkAutoGain();
    const samples = new Float32Array(960).fill(0.25);

    const result = gain.process(samples, 0.25);

    expect(result.gain).toBe(1);
    expect(result.samples).toBe(samples);
    expect(result.peak).toBeCloseTo(0.25, 5);
  });

  it('limits amplified samples below full scale', () => {
    const gain = createMicUplinkAutoGain();
    const quiet = new Float32Array(960).fill(0.001);
    for (let i = 0; i < 12; i++) gain.process(quiet, 0.001);

    const hotTransient = new Float32Array(960).fill(1);
    const result = gain.process(hotTransient, 1);

    expect(peak(result.samples)).toBeLessThanOrEqual(0.981);
  });
});
