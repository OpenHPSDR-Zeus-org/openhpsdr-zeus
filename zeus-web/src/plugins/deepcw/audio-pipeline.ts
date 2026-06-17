// SPDX-License-Identifier: AGPL-3.0-only
//
// Audio plumbing between the Zeus RX audio bus (48 kHz float mono) and the
// DeepCW model (which expects a fixed lower rate — 3200 Hz per the engine
// metadata). We keep a rolling window of source-rate audio and resample the
// whole window to the model rate at decode time, so resampling stays
// phase-continuous and bit-faithful to the engine's batch `resampleLinear`
// (examples/nodejs/decode_morse.mjs) rather than glueing per-frame chunks.

/** Linear-interpolation resampler — a direct port of the deepcw-engine
 *  reference so model input matches what the model was trained against. */
export function resampleLinear(
  audio: Float32Array,
  sourceRate: number,
  targetRate: number,
): Float32Array {
  if (sourceRate === targetRate) return audio;
  const targetLength = Math.round((audio.length * targetRate) / sourceRate);
  const output = new Float32Array(targetLength);
  for (let i = 0; i < targetLength; i += 1) {
    const sourcePosition = (i * sourceRate) / targetRate;
    const left = Math.floor(sourcePosition);
    const right = Math.min(left + 1, audio.length - 1);
    const fraction = sourcePosition - left;
    output[i] = audio[left]! * (1 - fraction) + audio[right]! * fraction;
  }
  return output;
}

/**
 * Fixed-length rolling buffer of source-rate samples. New audio shifts the
 * window left and appends at the tail (oldest samples fall off the front),
 * mirroring the engine web app's `audioCallback`. `version` bumps on every
 * append so consumers can detect new audio cheaply.
 */
export class RollingAudioBuffer {
  readonly samples: Float32Array;
  version = 0;
  /** How many real samples have been written (caps at capacity). */
  private filled = 0;

  constructor(public readonly capacity: number) {
    this.samples = new Float32Array(capacity);
  }

  /** Append a mono chunk (already at source rate). Long chunks keep only
   *  their most recent `capacity` samples. */
  push(chunk: Float32Array): void {
    const chunkLen = chunk.length;
    if (chunkLen === 0) return;
    const cap = this.samples.length;
    if (chunkLen >= cap) {
      this.samples.set(chunk.subarray(chunkLen - cap));
      this.filled = cap;
    } else {
      this.samples.copyWithin(0, chunkLen);
      this.samples.set(chunk, cap - chunkLen);
      this.filled = Math.min(cap, this.filled + chunkLen);
    }
    this.version += 1;
  }

  /** Seconds of real audio buffered so far (at the given source rate). */
  secondsFilled(sourceRate: number): number {
    return this.filled / sourceRate;
  }

  reset(): void {
    this.samples.fill(0);
    this.filled = 0;
    this.version += 1;
  }
}
