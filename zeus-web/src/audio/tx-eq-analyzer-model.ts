// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Pure geometry/DSP helpers for the TX parametric-EQ analyzer. Kept free of
// React / canvas / Web-Audio so the maths is unit-testable and identical on
// every platform Zeus runs on.
//
// Coordinate model: the analyzer paints a log-frequency X axis (voice band)
// and a linear-dBFS Y axis. The live spectrum uses the dBFS scale; the EQ
// response curve uses a symmetric ±gain scale centred on the unity line so a
// band cut/boost reads as a dip/bump regardless of the absolute spectrum.

/** Lowest frequency drawn on the X axis (Hz). */
export const F_MIN = 30;
/** Highest frequency drawn on the X axis (Hz) — covers the SSB voice band. */
export const F_MAX = 6000;
/** Spectrum floor (dBFS) at the bottom of the canvas. */
export const DB_MIN = -100;
/** Spectrum ceiling (dBFS) at the top of the canvas. */
export const DB_MAX = 0;
/** Half-range of the EQ gain scale (dB) — unity sits at the vertical centre. */
export const EQ_GAIN_RANGE = 18;
/** Fraction of canvas height the ±EQ_GAIN_RANGE swing occupies (each side). */
const EQ_GAIN_SPAN = 0.42;

const LOG_F_MIN = Math.log(F_MIN);
const LOG_F_MAX = Math.log(F_MAX);

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

/** Map a frequency (Hz) to a normalised [0,1] X position on the log axis. */
export function freqToNorm(hz: number): number {
  if (!(hz > 0)) return 0;
  return clamp((Math.log(hz) - LOG_F_MIN) / (LOG_F_MAX - LOG_F_MIN), 0, 1);
}

/** Map a frequency (Hz) to a pixel X within [x0, x0+width). */
export function freqToX(hz: number, x0: number, width: number): number {
  return x0 + freqToNorm(hz) * width;
}

/** Inverse of {@link freqToNorm} — normalised X back to frequency (Hz). */
export function normToFreq(norm: number): number {
  return Math.exp(LOG_F_MIN + clamp(norm, 0, 1) * (LOG_F_MAX - LOG_F_MIN));
}

/** Map a spectrum level (dBFS) to a pixel Y within [y0, y0+height). */
export function dbToY(db: number, y0: number, height: number): number {
  const t = clamp((db - DB_MIN) / (DB_MAX - DB_MIN), 0, 1);
  return y0 + (1 - t) * height;
}

/** Map an EQ gain (dB, signed) to a pixel Y within [y0, y0+height). */
export function gainToY(gainDb: number, y0: number, height: number): number {
  const t = clamp(gainDb / EQ_GAIN_RANGE, -1, 1);
  return y0 + height * (0.5 - t * EQ_GAIN_SPAN);
}

/** Centre frequency (Hz) of FFT bin `i` for the given size and sample rate. */
export function binToFreq(i: number, fftSize: number, sampleRate: number): number {
  return (i * sampleRate) / fftSize;
}

/**
 * Resample a linear-frequency FFT magnitude array (dB per bin) onto a
 * log-frequency pixel column array of length `width`. Each output column holds
 * the peak dB of every bin that falls within it; columns with no bin (sparse
 * low-frequency region) are linearly interpolated from their populated
 * neighbours so the trace stays continuous.
 *
 * @param db        FFT magnitudes in dB (length = fftSize/2), as produced by
 *                  AnalyserNode.getFloatFrequencyData.
 * @param width     number of output pixel columns.
 * @param sampleRate analyser sample rate (Hz).
 * @param fftSize   analyser fftSize (db.length * 2).
 * @param out       optional reusable Float32Array(width) to avoid allocation.
 */
export function binsToColumns(
  db: Float32Array,
  width: number,
  sampleRate: number,
  fftSize: number,
  out?: Float32Array,
): Float32Array {
  const cols = out && out.length === width ? out : new Float32Array(width);
  const filled = new Uint8Array(width);
  cols.fill(DB_MIN);

  const bins = db.length;
  for (let i = 1; i < bins; i += 1) {
    const f = binToFreq(i, fftSize, sampleRate);
    if (f < F_MIN || f > F_MAX) continue;
    const x = Math.min(width - 1, Math.max(0, Math.round(freqToNorm(f) * (width - 1))));
    const v = db[i] ?? DB_MIN;
    if (!filled[x] || v > (cols[x] ?? DB_MIN)) {
      cols[x] = v;
      filled[x] = 1;
    }
  }

  // Interpolate across runs of empty columns (low-freq region where one bin
  // spans many pixels).
  let prev = -1;
  for (let x = 0; x < width; x += 1) {
    if (!filled[x]) continue;
    if (prev >= 0 && x - prev > 1) {
      const a = cols[prev] ?? DB_MIN;
      const b = cols[x] ?? DB_MIN;
      for (let k = prev + 1; k < x; k += 1) {
        cols[k] = a + ((b - a) * (k - prev)) / (x - prev);
      }
    }
    prev = x;
  }
  return cols;
}

export interface EqBand {
  /** Centre frequency (Hz). */
  freqHz: number;
  /** Makeup / post gain applied by the band (dB, signed). */
  postGainDb: number;
}

export interface EqCurveInput {
  /** TX bandpass low edge (Hz). */
  filterLowHz: number;
  /** TX bandpass high edge (Hz). */
  filterHighHz: number;
  /** Per-band makeup gains (the CFC bands). */
  bands: readonly EqBand[];
  /** Whether the per-band processor is engaged (CFC master enable). */
  bandsEnabled: boolean;
}

// 1/3-octave-ish bell width in natural-log frequency units.
const BELL_SIGMA = 0.32;
// Bandpass skirt steepness (dB per octave outside the passband).
const SKIRT_DB_PER_OCT = 18;

/**
 * Build the TX EQ response curve (gain in dB) over a `width`-length log-X grid.
 * Combines the bandpass passband (flat inside, sloped skirts outside) with a
 * Gaussian bell per active band weighted by its makeup gain — the same shaping
 * the operator hears on the air.
 */
export function buildEqCurve(input: EqCurveInput, width: number, out?: Float32Array): Float32Array {
  const curve = out && out.length === width ? out : new Float32Array(width);
  const lo = Math.min(input.filterLowHz, input.filterHighHz);
  const hi = Math.max(input.filterLowHz, input.filterHighHz);

  for (let x = 0; x < width; x += 1) {
    const f = normToFreq(width <= 1 ? 0 : x / (width - 1));
    let g = 0;

    // Bandpass skirts: roll off outside the passband edges.
    if (lo > 0 && f < lo) g -= SKIRT_DB_PER_OCT * Math.log2(lo / f);
    if (hi > 0 && f > hi) g -= SKIRT_DB_PER_OCT * Math.log2(f / hi);

    // Per-band makeup bells.
    if (input.bandsEnabled) {
      for (const band of input.bands) {
        if (!(band.freqHz > 0) || band.postGainDb === 0) continue;
        const d = Math.log(f / band.freqHz) / BELL_SIGMA;
        g += band.postGainDb * Math.exp(-0.5 * d * d);
      }
    }

    curve[x] = g;
  }
  return curve;
}

/**
 * Treat WDSP stage levels at or below this dBFS as "bypassed/idle" rather than
 * a genuine reading (mirrors the TxMetersV2 sentinel convention).
 */
export const STAGE_BYPASS_DBFS = -200;

/** Format a stage dBFS value for the telemetry strip, or '—' when bypassed. */
export function formatStageDb(db: number): string {
  if (!Number.isFinite(db) || db <= STAGE_BYPASS_DBFS) return '—';
  return db.toFixed(1);
}
