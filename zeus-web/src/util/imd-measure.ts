// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Two-tone IMD measurement — ported from Thetis (display.cs two_tone_readings
// + findImd, MW0LGE). Given a panadapter spectrum (dBm-per-pixel) and its
// frequency axis, locate the two fundamental tones and their 3rd/5th-order
// intermodulation products, then report IMD3/IMD5 suppression in dBc and the
// output intercept points (OIP3/OIP5).
//
// The algorithm is source-agnostic: it measures whatever spectrum is on the
// panadapter. To measure *PA* IMD (what PureSignal corrects) the operator
// views the post-PA feedback / a monitoring RX; the pre-PA TX-IQ analyzer
// shows only the clean digital tones.

export interface ImdInput {
  /** dBm per pixel, length === width. Bin x maps to centerHz + (x - width/2)·hzPerPixel. */
  db: ArrayLike<number>;
  width: number;
  /** Hz at the centre pixel (width/2). */
  centerHz: number;
  hzPerPixel: number;
  /** Optional local two-tone generator spacing, used to avoid picking IMD/spurs as fundamentals. */
  expectedToneSpacingHz?: number;
}

export interface ImdProduct {
  lowerDbm: number;
  upperDbm: number;
  /** Suppression below the weaker fundamental, dB. Positive = product is down. */
  dbc: number;
  lowerHz: number;
  upperHz: number;
}

export interface ImdReadout {
  ok: true;
  f0LowerDbm: number;
  f0UpperDbm: number;
  f0LowerHz: number;
  f0UpperHz: number;
  toneSpacingHz: number;
  imd3: ImdProduct;
  imd5: ImdProduct;
  /** Output 3rd/5th-order intercept points, dBm. */
  oip3: number;
  oip5: number;
}

export interface ImdMiss {
  ok: false;
  /** Human-facing reason the readings couldn't be taken. */
  reason: string;
}

export type ImdResult = ImdReadout | ImdMiss;

interface Peak {
  x: number;
  dbm: number;
}

interface FundamentalPair {
  low: Peak;
  high: Peak;
  spacingPenaltyPx: number;
}

interface CandidateReadout {
  readout: ImdReadout;
  score: number;
}

// Tones closer than this (pixels) can't be resolved — Thetis uses the same
// pixel_diff > 10 gate before attempting to place the IMD products.
const MIN_TONE_SEP_PX = 11;
const MAX_EXPECTED_PAIR_CANDIDATES = 24;

function validInputWidth(input: ImdInput): number {
  const { db, width, centerHz, hzPerPixel } = input;
  if (
    !Number.isFinite(width) ||
    !Number.isInteger(width) ||
    width < 16 ||
    width > db.length ||
    !Number.isFinite(centerHz) ||
    !Number.isFinite(hzPerPixel) ||
    hzPerPixel <= 0
  ) {
    return 0;
  }
  return width;
}

/** All local maxima, strongest first. Noise bumps are fine — findImd only
 *  considers candidates near each predicted product position. */
function findPeaks(db: ArrayLike<number>, width: number): Peak[] {
  const peaks: Peak[] = [];
  for (let i = 1; i < width - 1; i++) {
    const v = db[i];
    const prev = db[i - 1];
    const next = db[i + 1];
    if (v === undefined || prev === undefined || next === undefined) continue;
    // Strict on the left, >= on the right so a flat-topped blob registers once.
    if (v > prev && v >= next && Number.isFinite(v)) {
      peaks.push({ x: i, dbm: v });
    }
  }
  peaks.sort((a, b) => b.dbm - a.dbm);
  return peaks;
}

// Port of Thetis findImd: search ±(pixelJump/4) around the predicted position
// for the strongest peak. imd 1 = fundamental (jump 0), 3 = IMD3 (jump 1),
// 5 = IMD5 (jump 2). Products sit one tone-spacing apart, outward from each
// fundamental.
function findImd(
  group: Peak[],
  imd: number,
  pixelJump: number,
  offset: number,
  low: boolean,
): Peak | null {
  const jump = (imd - 1) / 2;
  const estimate = low ? offset - jump * pixelJump : offset + jump * pixelJump;
  const searchRange = pixelJump / 4;
  let best: Peak | null = null;
  let bestDist = Infinity;
  for (const p of group) {
    const dist = Math.abs(p.x - estimate);
    if (dist <= searchRange) {
      if (best === null || p.dbm > best.dbm || (p.dbm === best.dbm && dist < bestDist)) {
        best = p;
        bestDist = dist;
      }
    }
  }
  return best;
}

function pairFromPeaks(a: Peak, b: Peak, spacingPenaltyPx = 0): FundamentalPair {
  return a.x <= b.x
    ? { low: a, high: b, spacingPenaltyPx }
    : { low: b, high: a, spacingPenaltyPx };
}

function defaultFundamentalPair(peaks: Peak[]): FundamentalPair | null {
  const f0 = peaks[0];
  if (f0 === undefined) return null;
  for (let i = 1; i < peaks.length; i++) {
    const p = peaks[i];
    if (p === undefined) continue;
    if (Math.abs(p.x - f0.x) >= MIN_TONE_SEP_PX) return pairFromPeaks(f0, p);
  }
  return null;
}

function expectedSpacingPx(input: ImdInput): number | null {
  const expectedHz = input.expectedToneSpacingHz;
  if (!Number.isFinite(expectedHz) || expectedHz === undefined || expectedHz <= 0) return null;
  const px = Math.abs(expectedHz / input.hzPerPixel);
  return px >= MIN_TONE_SEP_PX ? px : null;
}

function expectedFundamentalPairs(peaks: Peak[], expectedPx: number): FundamentalPair[] {
  const tolerancePx = Math.max(3, expectedPx * 0.08);
  const search = peaks.slice(0, Math.min(peaks.length, MAX_EXPECTED_PAIR_CANDIDATES));
  const pairs: FundamentalPair[] = [];
  for (let i = 0; i < search.length; i++) {
    const a = search[i];
    if (!a) continue;
    for (let j = i + 1; j < search.length; j++) {
      const b = search[j];
      if (!b) continue;
      const distancePx = Math.abs(a.x - b.x);
      const spacingPenaltyPx = Math.abs(distancePx - expectedPx);
      if (distancePx >= MIN_TONE_SEP_PX && spacingPenaltyPx <= tolerancePx) {
        pairs.push(pairFromPeaks(a, b, spacingPenaltyPx));
      }
    }
  }
  pairs.sort((a, b) => {
    const spacing = a.spacingPenaltyPx - b.spacingPenaltyPx;
    if (spacing !== 0) return spacing;
    return Math.min(b.low.dbm, b.high.dbm) - Math.min(a.low.dbm, a.high.dbm);
  });
  return pairs;
}

function measurePair(
  peaks: Peak[],
  pair: FundamentalPair,
  centerHz: number,
  hzPerPixel: number,
  width: number,
): CandidateReadout | null {
  const lowX = pair.low.x;
  const highX = pair.high.x;
  const pixelDiff = highX - lowX;
  if (pixelDiff <= 10) return null;

  const midX = lowX + pixelDiff / 2;
  const lowGroup = peaks.filter((p) => p.x < midX);
  const highGroup = peaks.filter((p) => p.x > midX);

  const fL = findImd(lowGroup, 1, pixelDiff, lowX, true);
  const fH = findImd(highGroup, 1, pixelDiff, highX, false);
  const i3L = findImd(lowGroup, 3, pixelDiff, lowX, true);
  const i3H = findImd(highGroup, 3, pixelDiff, highX, false);
  const i5L = findImd(lowGroup, 5, pixelDiff, lowX, true);
  const i5H = findImd(highGroup, 5, pixelDiff, highX, false);
  if (!fL || !fH || !i3L || !i3H || !i5L || !i5H) return null;

  const dbcMin = Math.min(fL.dbm, fH.dbm);
  const imd3max = Math.max(i3L.dbm, i3H.dbm);
  const imd5max = Math.max(i5L.dbm, i5H.dbm);
  const imd3dBc = dbcMin - imd3max;
  const imd5dBc = dbcMin - imd5max;
  const oip3 = dbcMin + imd3dBc / 2;
  const oip5 = dbcMin + imd5dBc / 2;
  const hz = (x: number) => centerHz + (x - width / 2) * hzPerPixel;
  const balancePenalty = Math.abs(fL.dbm - fH.dbm) * 0.2;
  const productScore = Math.max(-20, Math.min(60, imd3dBc)) * 0.25
    + Math.max(-20, Math.min(80, imd5dBc)) * 0.1;
  const readout: ImdReadout = {
    ok: true,
    f0LowerDbm: fL.dbm,
    f0UpperDbm: fH.dbm,
    f0LowerHz: hz(fL.x),
    f0UpperHz: hz(fH.x),
    toneSpacingHz: Math.abs(hz(fH.x) - hz(fL.x)),
    imd3: {
      lowerDbm: i3L.dbm,
      upperDbm: i3H.dbm,
      dbc: imd3dBc,
      lowerHz: hz(i3L.x),
      upperHz: hz(i3H.x),
    },
    imd5: {
      lowerDbm: i5L.dbm,
      upperDbm: i5H.dbm,
      dbc: imd5dBc,
      lowerHz: hz(i5L.x),
      upperHz: hz(i5H.x),
    },
    oip3,
    oip5,
  };

  return {
    readout,
    score: Math.min(fL.dbm, fH.dbm) + productScore - balancePenalty - pair.spacingPenaltyPx,
  };
}

/**
 * Locate the two fundamentals + IMD3/IMD5 products in a panadapter spectrum and
 * compute IMD suppression. Returns {ok:false, reason} when the tones can't be
 * resolved (no signal, tones merged, or zoomed too far out).
 */
export function computeImd(input: ImdInput): ImdResult {
  const { db, centerHz, hzPerPixel } = input;
  const width = validInputWidth(input);
  if (width === 0) return { ok: false, reason: 'no spectrum' };

  const peaks = findPeaks(db, width);
  if (peaks.length < 2) return { ok: false, reason: 'no signal' };

  const expectedPx = expectedSpacingPx(input);
  if (expectedPx !== null) {
    const candidates = expectedFundamentalPairs(peaks, expectedPx);
    if (candidates.length === 0) {
      return { ok: false, reason: 'two-tone spacing not found — adjust span' };
    }

    const measured = candidates
      .map((pair) => measurePair(peaks, pair, centerHz, hzPerPixel, width))
      .filter((r): r is CandidateReadout => r !== null)
      .sort((a, b) => b.score - a.score);
    const best = measured[0];
    if (best) return best.readout;
    return { ok: false, reason: 'IMD peaks off-screen — widen span' };
  }

  const pair = defaultFundamentalPair(peaks);
  if (!pair) return { ok: false, reason: 'tones merged — increase zoom' };
  const measured = measurePair(peaks, pair, centerHz, hzPerPixel, width);
  if (!measured) return { ok: false, reason: 'IMD peaks off-screen — widen span' };
  return measured.readout;
}
