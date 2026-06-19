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
// Zeus is an independent reimplementation in .NET — not a fork. See
// ATTRIBUTIONS.md for the full provenance statement.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Animated display zoom — the scale counterpart to view-center's pan glide.
//
// The server changes zoom in discrete integer steps; each step jumps
// hzPerPixel (span = sampleRate / zoom / Width). Snapping the panadapter and
// waterfall to the new span every step reads as a hard stretch (and, for the
// waterfall, a one-shot resample of the whole history). This module eases the
// DISPLAYED hzPerPixel toward the target so both surfaces scale smoothly.
//
// State-of-the-art rendering note: the displayed zoom is applied as a
// draw-time SAMPLING TRANSFORM in the fragment/vertex shaders (a scale around
// the view centre), NOT by re-resampling the history texture each frame. That
// keeps it O(1) per pixel with no extra GL passes and — unlike repeated
// texture resamples — never accumulates bilinear blur. The waterfall history
// is rebased to the server span exactly once per zoom step (the existing
// 'rescale' path); this module only animates how that texture is VIEWED.
//
// Zoom is a single global setting applied to every receiver channel
// (DspPipelineService applies SetZoom to RX1 and RX2 together and both frames
// carry the same hzPerPixel), so one shared tween drives all spectrum
// surfaces. During steady RX the loop is parked — zero idle cost.
//
// Kill switch: VIEW_ZOOM_TWEEN_ENABLED = false (or TAU_MS = 0) snaps to the
// target every tick, restoring the pre-animation stepping feel.

export const VIEW_ZOOM_TWEEN_ENABLED = true;

// Tween time-constant. A touch slower than the pan glide (view-center, 70 ms)
// so a multi-level zoom jump reads as a graceful scale rather than a snap;
// converges within ~3τ after the last step.
const TAU_MS = 90;
// Clamp dt so a GC pause / background-tab wakeup doesn't integrate one huge
// step (the tween would visibly teleport). 50 ms ≈ 3 missed vsyncs.
const MAX_DT_MS = 50;
// Relative park threshold. hzPerPixel spans ~32× across the zoom range, so a
// fixed Hz epsilon would be wrong at one end; 0.2% of the target is the
// sub-pixel-invisible bound at any zoom.
const SNAP_EPS_REL = 0.002;

type Listener = () => void;

// Injectable clock/raf so the tween is unit-testable without a browser.
type Clock = () => number;
type Raf = (cb: (t: number) => void) => number;
type CancelRaf = (h: number) => void;

let now: Clock = () => performance.now();
let raf: Raf = (cb) => requestAnimationFrame(cb);
let cancelRaf: CancelRaf = (h) => cancelAnimationFrame(h);

let initialized = false;
let targetHzPerPixel = 0;
let displayedHzPerPixel = 0;
let rafHandle = 0;
let lastTickMs = 0;
const listeners = new Set<Listener>();

function notify(): void {
  for (const cb of listeners) {
    try {
      cb();
    } catch (err) {
      // One bad listener must not stall the tween loop.

      console.error('view-zoom listener threw', err);
    }
  }
}

function converged(): boolean {
  if (targetHzPerPixel <= 0) return true;
  return Math.abs(targetHzPerPixel - displayedHzPerPixel) <= targetHzPerPixel * SNAP_EPS_REL;
}

function tick(tMs: number): void {
  rafHandle = 0;
  const dt = Math.min(MAX_DT_MS, Math.max(0, tMs - lastTickMs));
  lastTickMs = tMs;
  if (!VIEW_ZOOM_TWEEN_ENABLED || TAU_MS <= 0 || converged()) {
    displayedHzPerPixel = targetHzPerPixel; // converged (or kill switch) — park.
    notify();
    return;
  }
  displayedHzPerPixel += (targetHzPerPixel - displayedHzPerPixel) * (1 - Math.exp(-dt / TAU_MS));
  notify();
  rafHandle = raf(tick);
}

function ensureRunning(): void {
  if (rafHandle !== 0) return;
  lastTickMs = now();
  rafHandle = raf(tick);
}

/** Hard-set displayed == target == hzPerPixel with no glide. Used on the
 *  first frame and on hard resets (reconnect, width change, context restore)
 *  where there's no prior scale to ease from. */
export function snapTo(hzPerPixel: number): void {
  if (!(hzPerPixel > 0)) return;
  targetHzPerPixel = hzPerPixel;
  displayedHzPerPixel = hzPerPixel;
  initialized = true;
  notify();
}

/** Ease the displayed zoom toward a new server span. No-op when the span is
 *  unchanged (steady RX), so this is safe to call on every frame. */
export function setTarget(hzPerPixel: number): void {
  if (!(hzPerPixel > 0)) return;
  if (!initialized) {
    snapTo(hzPerPixel);
    return;
  }
  if (Math.abs(hzPerPixel - targetHzPerPixel) <= targetHzPerPixel * SNAP_EPS_REL) return;
  targetHzPerPixel = hzPerPixel;
  ensureRunning();
}

/** The animated hzPerPixel the surfaces should render against. */
export function getDisplayedHzPerPixel(): number {
  return displayedHzPerPixel;
}

/** The server span the view is easing toward. */
export function getTargetHzPerPixel(): number {
  return targetHzPerPixel;
}

/** False until the first frame establishes a span. Surfaces render at unit
 *  scale (the frame's own hzPerPixel) until then. */
export function isInitialized(): boolean {
  return initialized;
}

/** True while the displayed zoom is still easing toward the target. */
export function isAnimating(): boolean {
  return initialized && !converged();
}

/** Subscribe to zoom motion. Fires once per tween tick (display rate while
 *  animating; silent when parked). Returns the unsubscribe fn. */
export function subscribe(cb: Listener): () => void {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

// ---------------------------------------------------------------------------
// Test hooks — not part of the public surface.

export function _setClockForTest(clock: Clock, rafImpl: Raf, cancelImpl: CancelRaf): void {
  now = clock;
  raf = rafImpl;
  cancelRaf = cancelImpl;
}

export function _resetForTest(): void {
  if (rafHandle !== 0) cancelRaf(rafHandle);
  rafHandle = 0;
  initialized = false;
  targetHzPerPixel = 0;
  displayedHzPerPixel = 0;
  lastTickMs = 0;
  listeners.clear();
}

export function _isLoopRunningForTest(): boolean {
  return rafHandle !== 0;
}
