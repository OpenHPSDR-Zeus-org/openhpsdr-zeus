// SPDX-License-Identifier: GPL-2.0-or-later
//
// Browser/mobile mic input can arrive far below desktop capture levels when
// mobile OS AGC is unavailable or disabled by getUserMedia constraints. This
// source-side gain stage raises only quiet browser uplink blocks before they
// enter the server TXA chain; server mic gain and WDSP processing still apply.

export type MicUplinkGainResult = {
  samples: Float32Array;
  peak: number;
  gain: number;
};

const TARGET_PEAK = 0.2;          // -14 dBFS voice peaks
const MAX_GAIN = 256;             // +48 dB ceiling for very quiet phone mics
const LIMIT = 0.98;
const ATTACK = 0.35;
const RELEASE = 0.6;
const MIN_TRACKABLE_PEAK = 1e-5;  // below -100 dBFS, treat as silence

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Number.isFinite(value) ? value : min));
}

function measuredPeak(samples: Float32Array): number {
  let peak = 0;
  for (let i = 0; i < samples.length; i++) {
    const v = Math.abs(samples[i] ?? 0);
    if (Number.isFinite(v) && v > peak) peak = v;
  }
  return peak;
}

export function createMicUplinkAutoGain() {
  let gain = 1;

  return {
    process(samples: Float32Array, reportedPeak: number): MicUplinkGainResult {
      const inputPeak = clamp(
        reportedPeak > 0 ? reportedPeak : measuredPeak(samples),
        0,
        1,
      );

      if (inputPeak >= MIN_TRACKABLE_PEAK) {
        const desired = clamp(TARGET_PEAK / inputPeak, 1, MAX_GAIN);
        const coeff = desired > gain ? ATTACK : RELEASE;
        gain += (desired - gain) * coeff;
        gain = clamp(gain, 1, MAX_GAIN);
      }

      if (gain <= 1.0001) {
        return { samples, peak: inputPeak, gain };
      }

      const amplified = new Float32Array(samples.length);
      let outputPeak = 0;
      for (let i = 0; i < samples.length; i++) {
        const sample = samples[i] ?? 0;
        const finite = Number.isFinite(sample) ? sample : 0;
        const v = clamp(finite * gain, -LIMIT, LIMIT);
        amplified[i] = v;
        const abs = Math.abs(v);
        if (abs > outputPeak) outputPeak = abs;
      }
      return { samples: amplified, peak: outputPeak, gain };
    },

    reset(): void {
      gain = 1;
    },

    currentGain(): number {
      return gain;
    },
  };
}
