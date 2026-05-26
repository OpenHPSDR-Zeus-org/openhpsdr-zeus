// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/**
 * Goertzel filter — efficient single-tone detection.
 *
 * Computes a single DFT bin without the O(N log N) cost of a full FFT.
 * For CW, we know the approximate pitch (600-800 Hz), so we can detect
 * just that frequency.
 *
 * Algorithm:
 *   y[n] = x[n] + 2*cos(ω)*y[n-1] - y[n-2]
 *   where ω = 2π * targetFreq / sampleRate
 *
 * The magnitude is computed from the last two filter outputs:
 *   magnitude = sqrt(y[n]^2 + y[n-1]^2 - 2*cos(ω)*y[n]*y[n-1])
 *
 * See: http://en.wikipedia.org/wiki/Goertzel_algorithm
 */
export class GoertzelFilter {
  private coeff: number;
  private y0 = 0;
  private y1 = 0;

  constructor(
    private targetFreq: number,
    private sampleRate: number,
  ) {
    const omega = (2 * Math.PI * targetFreq) / sampleRate;
    this.coeff = 2 * Math.cos(omega);
  }

  /** Process a single sample and return the current magnitude. */
  process(sample: number): number {
    const y2 = this.y1;
    this.y1 = this.y0;
    // y[n] = x[n] + coeff * y[n-1] - y[n-2]
    this.y0 = sample + this.coeff * this.y1 - y2;
    return this.getMagnitude();
  }

  /** Compute the magnitude from the current filter state. */
  getMagnitude(): number {
    // magnitude = sqrt(y0^2 + y1^2 - coeff*y0*y1)
    const term1 = this.y0 * this.y0 + this.y1 * this.y1;
    const term2 = this.coeff * this.y0 * this.y1;
    return Math.sqrt(Math.max(0, term1 - term2));
  }

  /** Reset filter state for a new detection cycle. */
  reset(): void {
    this.y0 = 0;
    this.y1 = 0;
  }

  /** Update target frequency (for when operator changes pitch). */
  setTargetFreq(freq: number): void {
    if (freq === this.targetFreq) return;
    this.targetFreq = freq;
    const omega = (2 * Math.PI * freq) / this.sampleRate;
    this.coeff = 2 * Math.cos(omega);
    this.reset();
  }
}

/**
 * Adaptive threshold detector.
 *
 * Computes a running average of the signal magnitude and sets a threshold
 * as `noiseFloor + margin`. This adapts to changing signal strength.
 */
export class AdaptiveThreshold {
  private sum = 0;
  private count = 0;
  private noiseFloor = 0;
  private signalPeak = 0;

  constructor(
    private windowSize: number = 100, // samples to average
    private marginRatio: number = 0.3, // threshold = noiseFloor * (1 + marginRatio)
  ) {}

  /** Process a magnitude value and return whether it's above threshold. */
  process(magnitude: number): boolean {
    // Running average
    this.sum += magnitude;
    this.count++;

    if (this.count >= this.windowSize) {
      const avg = this.sum / this.count;
      // Noise floor is the 25th percentile-ish (lower quartile)
      this.noiseFloor = avg * 0.75;
      // Signal peak decays slowly
      this.signalPeak = Math.max(this.signalPeak * 0.99, avg);
      this.sum = 0;
      this.count = 0;
    }

    const threshold = this.noiseFloor * (1 + this.marginRatio);
    return magnitude > threshold && magnitude > this.noiseFloor * 2;
  }

  /** Get current threshold value. */
  getThreshold(): number {
    return this.noiseFloor * (1 + this.marginRatio);
  }

  /** Get estimated SNR in dB (relative to noise floor). */
  getSnrDb(): number {
    if (this.noiseFloor < 1e-6) return 0;
    return 10 * Math.log10(this.signalPeak / this.noiseFloor);
  }

  /** Reset state. */
  reset(): void {
    this.sum = 0;
    this.count = 0;
    this.noiseFloor = 0;
    this.signalPeak = 0;
  }
}

/**
 * Timing estimator — adapts to operator's "fist".
 *
 * Collects on-key durations in a rolling window and estimates the reference
 * dit duration. Handles irregular human timing (not PARIS-perfect).
 */
export class TimingEstimator {
  private durations: number[] = [];
  private ditMs = 24; // Default for 20 WPM (1200/20/2 = 30 ms per unit, dit = 1 unit ≈ 24 ms)
  private dahThreshold = 36; // 1.5 * ditMs
  private elementGapThreshold = 28.8; // 1.2 * ditMs
  private letterGapThreshold = 72; // 3.0 * ditMs
  private wordGapThreshold = 132; // 5.5 * ditMs

  constructor(private maxSamples: number = 20) {}

  /** Record an on-key duration in ms. */
  record(durationMs: number): void {
    this.durations.push(durationMs);
    if (this.durations.length > this.maxSamples) {
      this.durations.shift();
    }

    if (this.durations.length < 5) return; // Need enough samples to estimate

    // Compute histogram to separate dits from dahs
    const sorted = [...this.durations].sort((a, b) => a - b);
    // Lower half are likely dits (operators send more dits than dahs)
    const lowerHalf = sorted.slice(0, Math.floor(sorted.length / 2));
    if (lowerHalf.length === 0) return;

    const medianIdx = Math.floor(lowerHalf.length / 2);
    const median = lowerHalf[medianIdx];
    if (median === undefined) return;

    // Clamp to reasonable range (5-60 WPM → dit ∈ [10, 120] ms)
    const clamped = Math.max(10, Math.min(120, median));

    // Smooth transition
    this.ditMs = this.ditMs * 0.8 + clamped * 0.2;
    this.recomputeThresholds();
  }

  /** Recompute gap thresholds from current ditMs. */
  private recomputeThresholds(): void {
    this.dahThreshold = this.ditMs * 1.5;
    this.elementGapThreshold = this.ditMs * 1.2;
    this.letterGapThreshold = this.ditMs * 3.0;
    this.wordGapThreshold = this.ditMs * 5.5;
  }

  /** Determine if a duration is a dit or dah. */
  classify(durationMs: number): 'dit' | 'dah' {
    return durationMs < this.dahThreshold ? 'dit' : 'dah';
  }

  /** Classify a gap (silence between elements). */
  classifyGap(durationMs: number): 'element' | 'letter' | 'word' | 'unknown' {
    if (durationMs < this.elementGapThreshold) return 'element';
    if (durationMs < this.letterGapThreshold) return 'letter';
    if (durationMs < this.wordGapThreshold) return 'word';
    return 'unknown';
  }

  /** Get estimated WPM based on current dit duration. */
  getWpm(): number {
    // WPM = 1200 / (ditMs * 2) per standard timing
    return Math.round(1200 / (this.ditMs * 2));
  }

  /** Get current dit duration in ms. */
  getDitMs(): number {
    return this.ditMs;
  }

  /** Reset state. */
  reset(): void {
    this.durations = [];
    this.ditMs = 24;
    this.recomputeThresholds();
  }
}

/**
 * Morse code character mapping.
 */
export const MORSE_CODE: Readonly<Record<string, string>> = {
  'A': '.-',
  'B': '-...',
  'C': '-.-.',
  'D': '-..',
  'E': '.',
  'F': '..-.',
  'G': '--.',
  'H': '....',
  'I': '..',
  'J': '.---',
  'K': '-.-',
  'L': '.-..',
  'M': '--',
  'N': '-.',
  'O': '---',
  'P': '.--.',
  'Q': '--.-',
  'R': '.-.',
  'S': '...',
  'T': '-',
  'U': '..-',
  'V': '...-',
  'W': '.--',
  'X': '-..-',
  'Y': '-.--',
  'Z': '--..',
  '0': '-----',
  '1': '.----',
  '2': '..---',
  '3': '...--',
  '4': '....-',
  '5': '.....',
  '6': '-....',
  '7': '--...',
  '8': '---..',
  '9': '----.',
  '.': '.-.-.-',
  ',': '--..--',
  '?': '..--..',
  '/': '-..-.',
  '@': '.--.-.',
  '=': '-...-',
  '+': '.-.-.',
  '_': '..--.-',
  "'": '.----.',
  '()': '-.--.',
  'AR': '.-.-.',
  'KN': '-.--.',
  'SK': '...-.-',
};

// Reverse lookup for decoding
export const MORSE_DECODE: Readonly<Record<string, string>> = Object.fromEntries(
  Object.entries(MORSE_CODE).map(([char, code]) => [code, char]),
);

/**
 * Morse FSM state machine.
 *
 * Tracks the sequence of dits/dahs and gaps to assemble characters.
 */
export class MorseFsm {
  private currentPattern = '';
  private lastEdgeTime = 0;
  private elementGapStart = 0;
  private inWord = false;

  constructor(private timing: TimingEstimator) {}

  /** Process a key-down edge (tone detected). */
  onKeyDown(timeMs: number): string | null {
    // Check for gap between this and previous element
    if (this.elementGapStart > 0) {
      const gap = timeMs - this.elementGapStart;
      const gapType = this.timing.classifyGap(gap);

      if (gapType === 'letter' || gapType === 'word') {
        const char = this.emitCharacter();
        if (gapType === 'word') {
          this.inWord = false;
        }
        return char;
      }
    }

    this.lastEdgeTime = timeMs;
    return null;
  }

  /** Process a key-up edge (tone lost). */
  onKeyUp(timeMs: number): string | null {
    const duration = timeMs - this.lastEdgeTime;
    const element = this.timing.classify(duration);
    this.timing.record(duration);
    this.currentPattern += element === 'dit' ? '.' : '-';
    this.elementGapStart = timeMs;
    this.inWord = true;
    return null;
  }

  /** Emit the current character (letter gap detected). */
  private emitCharacter(): string | null {
    if (this.currentPattern === '') return null;
    const char = MORSE_DECODE[this.currentPattern] ?? '?';
    this.currentPattern = '';
    return char;
  }

  /** Emit a space (word gap). */
  emitSpace(): string | null {
    if (!this.inWord) return null;
    this.inWord = false;
    return ' ';
  }

  /** Force emit any pending character (end of stream). */
  flush(): string | null {
    return this.emitCharacter();
  }

  /** Reset state. */
  reset(): void {
    this.currentPattern = '';
    this.lastEdgeTime = 0;
    this.elementGapStart = 0;
    this.inWord = false;
  }
}

/**
 * CW decoder — orchestrates all components.
 */
export interface CwDecoderConfig {
  targetFreq: number; // Expected CW tone pitch (Hz)
  sampleRate: number; // Audio sample rate
}

export interface CwDecoderOutput {
  char: string; // Decoded character
  wpm: number; // Estimated WPM
  snrDb: number; // Signal-to-noise ratio estimate
  confidence: number; // 0.0-1.0 confidence metric
}

export class CwDecoder {
  private goertzel: GoertzelFilter;
  private threshold: AdaptiveThreshold;
  private timing: TimingEstimator;
  private fsm: MorseFsm;

  private isKeyed = false;
  private lastKeyedTime = 0;
  private lastCheckTime = 0;
  private keyDownDebounce = 3; // samples to debounce key-down

  constructor(config: CwDecoderConfig) {
    this.goertzel = new GoertzelFilter(config.targetFreq, config.sampleRate);
    this.threshold = new AdaptiveThreshold(100, 0.3);
    this.timing = new TimingEstimator(20);
    this.fsm = new MorseFsm(this.timing);
  }

  /** Process a single audio sample. Returns decoded character if any. */
  process(sample: number): CwDecoderOutput | null {
    const magnitude = this.goertzel.process(sample);
    const aboveThreshold = this.threshold.process(magnitude);
    const now = performance.now();

    // State machine transitions with debouncing
    if (aboveThreshold && !this.isKeyed) {
      // Potential key-down — verify it persists
      if (this.lastCheckTime > 0 && now - this.lastCheckTime < this.keyDownDebounce) {
        return null;
      }
      this.isKeyed = true;
      this.lastKeyedTime = now;
      const char = this.fsm.onKeyDown(now);
      if (char) return this.makeOutput(char);
    } else if (!aboveThreshold && this.isKeyed) {
      // Key-up
      this.isKeyed = false;
      const char = this.fsm.onKeyUp(now);
      if (char) return this.makeOutput(char);
    }

    // Check for gap timeout (emit pending character if silence too long)
    if (!this.isKeyed && this.lastKeyedTime > 0 && now - this.lastKeyedTime > this.timing.getDitMs() * 4) {
      const char = this.fsm.flush();
      if (char) {
        this.lastKeyedTime = 0;
        return this.makeOutput(char);
      }
    }

    this.lastCheckTime = now;
    return null;
  }

  private makeOutput(char: string): CwDecoderOutput {
    return {
      char,
      wpm: this.timing.getWpm(),
      snrDb: this.threshold.getSnrDb(),
      confidence: Math.min(1, Math.max(0, this.threshold.getSnrDb() / 10)),
    };
  }

  /** Update target frequency. */
  setTargetFreq(freq: number): void {
    this.goertzel.setTargetFreq(freq);
  }

  /** Reset all state. */
  reset(): void {
    this.goertzel.reset();
    this.threshold.reset();
    this.timing.reset();
    this.fsm.reset();
    this.isKeyed = false;
    this.lastKeyedTime = 0;
    this.lastCheckTime = 0;
  }

  /** Get current estimated WPM. */
  getWpm(): number {
    return this.timing.getWpm();
  }

  /** Get current SNR in dB. */
  getSnrDb(): number {
    return this.threshold.getSnrDb();
  }

  /** Get current confidence (0.0-1.0). */
  getConfidence(): number {
    return Math.min(1, Math.max(0, this.threshold.getSnrDb() / 10));
  }
}