// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// ft8-passband — PURE pixel <-> audio-offset geometry for the FT8/FT4 receive
// waterfall. The FT8 workspace reuses the shipping RF panadapter/waterfall
// surfaces (display-store frames). FT8 is USB, so the audio offset `o` Hz maps
// to RF `dial + o`. A click on the waterfall therefore resolves to an absolute
// RF frequency through the live slice geometry (centerHz/hzPerPixel/width), and
// the audio offset is `absHz - dialHz`, clamped to the 0..3000 Hz passband.
//
// These helpers are intentionally framework-free so the mapping can be unit
// tested without a renderer, and so the overlay and the marker math share ONE
// source of truth (they must never disagree — a click and the cursor it draws
// have to land on the same pixel). Mirrors readView()/SpotOverlay in the main
// app, scoped to the FT8 audio passband.

/** The FT8/FT4 audio passband. The decoder searches ~0..3000 Hz of USB audio. */
export const FT8_MIN_OFFSET_HZ = 0;
export const FT8_MAX_OFFSET_HZ = 3000;

/** The TX audio-offset range — narrower than the RX search span so a transmitted
 *  tone stays comfortably inside the SSB passband. This is the SINGLE source of
 *  truth for TX-offset clamping: the waterfall click, the OFFSET input, and the
 *  controller (`setTxFreq`) all clamp to it so click, type, and the value POSTed
 *  to the keyer always agree. Distinct from the wider RX `FT8_MAX_OFFSET_HZ`. */
export const FT8_MAX_TX_OFFSET_HZ = 2500;

/** Snap a waterfall click to a nearby decode when the cursor lands within this
 *  many screen pixels of a decoded signal's audio offset. Light, ham-friendly. */
export const DECODE_SNAP_RADIUS_PX = 30;

/** Live spectrum-slice geometry, read from the display-store slice that drives
 *  the panadapter/waterfall. `width` is the bin count and `hzPerPixel` the Hz
 *  per bin, so the total visible span is `width * hzPerPixel`. */
export interface PassbandView {
  centerHz: number;
  hzPerPixel: number;
  width: number;
}

/** Total visible span in Hz for a slice geometry (0 when geometry is absent). */
export function spanHzOf(view: PassbandView): number {
  const span = view.width * view.hzPerPixel;
  return Number.isFinite(span) && span > 0 ? span : 0;
}

/** Clamp an audio offset to the FT8/FT4 passband. */
export function clampOffsetHz(
  hz: number,
  min: number = FT8_MIN_OFFSET_HZ,
  max: number = FT8_MAX_OFFSET_HZ,
): number {
  if (!Number.isFinite(hz)) return min;
  return Math.min(max, Math.max(min, hz));
}

/** Absolute RF frequency under a client X coordinate, using the slice geometry.
 *  `rectLeft`/`rectWidth` describe the on-screen element the click landed on
 *  (its CSS pixel box), which is independent of the slice's bin `width`. */
export function absHzForClientX(
  clientX: number,
  rectLeft: number,
  rectWidth: number,
  view: PassbandView,
): number {
  const span = spanHzOf(view);
  if (span <= 0 || rectWidth <= 0) return view.centerHz;
  const frac = (clientX - rectLeft) / rectWidth;
  return view.centerHz + (frac - 0.5) * span;
}

/** Audio offset (Hz, clamped to the passband) under a client X coordinate. */
export function offsetHzForClientX(
  clientX: number,
  rectLeft: number,
  rectWidth: number,
  view: PassbandView,
  dialHz: number,
): number {
  const absHz = absHzForClientX(clientX, rectLeft, rectWidth, view);
  return clampOffsetHz(absHz - dialHz);
}

/** Inverse mapping: the pixel X (relative to the element's left edge, in the
 *  same `rectWidth` units passed in) for an audio offset. Pass `rectWidth=100`
 *  to get a left-percentage directly. Returns a value that may fall outside
 *  `[0, rectWidth]` when the offset is off-screen — callers gate visibility. */
export function pixelForOffsetHz(
  offsetHz: number,
  rectWidth: number,
  view: PassbandView,
  dialHz: number,
): number {
  const span = spanHzOf(view);
  if (span <= 0) return 0;
  const absHz = dialHz + offsetHz;
  const frac = 0.5 + (absHz - view.centerHz) / span;
  return frac * rectWidth;
}

/** True when an offset's marker would be visible in the current window. */
export function isOffsetVisible(
  offsetHz: number,
  view: PassbandView,
  dialHz: number,
): boolean {
  if (spanHzOf(view) <= 0) return false;
  const frac = pixelForOffsetHz(offsetHz, 1, view, dialHz);
  return frac >= 0 && frac <= 1;
}

/** Snap a clicked audio offset to the nearest decoded signal offset, when one
 *  sits within `radiusPx` screen pixels of the click. Past the radius the click
 *  stands (so clicking empty water tunes where you clicked). Pure: the caller
 *  supplies the decode offsets + the on-screen element width. */
export function snapOffsetToDecode(
  offsetHz: number,
  decodeOffsetsHz: readonly number[],
  rectWidth: number,
  view: PassbandView,
  radiusPx: number = DECODE_SNAP_RADIUS_PX,
): number {
  const span = spanHzOf(view);
  if (span <= 0 || rectWidth <= 0 || decodeOffsetsHz.length === 0) return offsetHz;
  const hzPerScreenPx = span / rectWidth;
  const radiusHz = radiusPx * hzPerScreenPx;
  let best = offsetHz;
  let bestDist = radiusHz;
  for (const d of decodeOffsetsHz) {
    const dist = Math.abs(d - offsetHz);
    if (dist <= bestDist) {
      bestDist = dist;
      best = d;
    }
  }
  return best;
}

/** The outcome of a waterfall click: the RX focus cursor ALWAYS moves; the TX
 *  audio offset moves UNLESS HOLD TX FREQ is engaged (then only RX moves). The
 *  controller double-guards `setTxFreq` when held, so `txOffsetHz` being null is
 *  belt-and-suspenders, not the sole guard. */
export interface WaterfallClickResult {
  rxFocusHz: number;
  txOffsetHz: number | null;
}

export function resolveWaterfallClick(
  offsetHz: number,
  holdTxFreq: boolean,
): WaterfallClickResult {
  // RX focus follows the full search span; the TX offset is clamped to the
  // narrower TX range so a click past it can't stage an out-of-band tone.
  return {
    rxFocusHz: offsetHz,
    txOffsetHz: holdTxFreq
      ? null
      : clampOffsetHz(offsetHz, FT8_MIN_OFFSET_HZ, FT8_MAX_TX_OFFSET_HZ),
  };
}
