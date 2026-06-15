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
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Animated view-center for the spectrum surfaces (issue #597 Phase 1).
//
// One smoothly-tweened center frequency drives the panadapter trace, the
// waterfall, the dial marker, the passband rect, and the freq-axis ticks as
// a single rigid body at display rate. Server frames (30 Hz) no longer drive
// horizontal motion — they only refresh content; every surface renders its
// data anchored at the frequency it was captured at, offset by
// (anchorHz − viewCenterHz) in fractional pixels.
//
// Model
// -----
// - `targetHz` is the *commanded* display center. Input handlers advance it
//   by DELTA (`nudgeTargetHz`) — delta-form is load-bearing: in CW the frame
//   center is the dial ∓ cw-pitch, and feeding any dial-space absolute value
//   in here would oscillate the display by ±pitch on every commit. Deltas
//   cancel the pitch term (constant while mode/pitch are unchanged).
// - `viewHz` eases toward `targetHz` with an exponential tween
//   (τ = TAU_MS), ticked on this module's own rAF loop. The loop self-parks
//   when |target − view| < SNAP_EPS_HZ — ZERO idle cost: no rAF, no
//   listeners firing, nothing. perf_pass_3 red-line respected.
// - `reconcileFrame` feeds every server frame's centerHz back in. While the
//   operator is actively tuning (recent optimistic stamp) frames lag the
//   command and are ignored. With no recent gesture, a frame-center move is
//   an EXTERNAL tune (CAT/TCI, band button, typed entry, mode change) — we
//   retarget and glide there, which also arms the refill hold so mislabeled
//   mid-retune frames are not adopted (see Panadapter.tsx).
// - `snapTo` is the hard-reset path (first frame, width change, or no overlap):
//   no glide, no hold — the next frame is authoritative.
//
// Kill switch: set VIEW_CENTER_TWEEN_ENABLED = false (or TAU_MS = 0) to
// snap-to-target every tick — restores today's stepping feel while keeping
// the anchor-model correctness fixes.

export const VIEW_CENTER_TWEEN_ENABLED = true;

// Tween time-constant. ~70 ms reads as "glides with the wheel" without
// feeling laggy; the display sits ~τ behind the commanded freq during a
// gesture and converges within ~3τ after the last input.
const TAU_MS = 70;
// Clamp dt so a GC pause / background-tab wakeup doesn't integrate one huge
// step (the tween would visibly teleport). 50 ms ≈ 3 missed vsyncs.
const MAX_DT_MS = 50;
// Park threshold. Converted to Hz via the live hzPerPixel so "0.05 px" is
// the actual sub-pixel invisibility bound at any zoom; falls back to 1 Hz
// when no frame geometry is known yet.
const SNAP_EPS_PX = 0.05;
// reconcileFrame treats a frame-center move as an external tune only when
// there has been no optimistic (operator) tune within this window. Frames
// lag commands by FFT-window + EMA + transport (~100-400 ms worst case).
const OPTIMISTIC_WINDOW_MS = 400;

type Listener = () => void;

// Injectable clock/raf so the tween is unit-testable without a browser.
type Clock = () => number;
type Raf = (cb: (t: number) => void) => number;
type CancelRaf = (h: number) => void;

let now: Clock = () => performance.now();
let raf: Raf = (cb) => requestAnimationFrame(cb);
let cancelRaf: CancelRaf = (h) => cancelAnimationFrame(h);

let initialized = false;
let targetHz = 0;
let viewHz = 0;
let hzPerPixel = 0;
let lastOptimisticTuneAtMs = -Infinity;
let lastTargetChangeAtMs = -Infinity;
let rafHandle = 0;
let lastTickMs = 0;
const listeners = new Set<Listener>();

function epsHz(): number {
  return hzPerPixel > 0 ? hzPerPixel * SNAP_EPS_PX : 1;
}

function notify(): void {
  for (const cb of listeners) {
    try {
      cb();
    } catch (err) {
      // One bad listener must not stall the tween loop.
      // eslint-disable-next-line no-console
      console.error('view-center listener threw', err);
    }
  }
}

function tick(tMs: number): void {
  rafHandle = 0;
  const dt = Math.min(MAX_DT_MS, Math.max(0, tMs - lastTickMs));
  lastTickMs = tMs;
  const gap = targetHz - viewHz;
  if (!VIEW_CENTER_TWEEN_ENABLED || TAU_MS <= 0 || Math.abs(gap) < epsHz()) {
    viewHz = targetHz; // converged (or kill switch) — park the loop.
    notify();
    return;
  }
  viewHz += gap * (1 - Math.exp(-dt / TAU_MS));
  notify();
  rafHandle = raf(tick);
}

function ensureRunning(): void {
  if (rafHandle !== 0) return;
  lastTickMs = now();
  rafHandle = raf(tick);
}

/** Advance the commanded display center by a delta (Hz). Delta-form only —
 *  see the module comment for why absolute dial values must never land here.
 *  Also stamps the optimistic-tune clock used by reconcileFrame and the
 *  App.tsx poll guard. */
export function nudgeTargetHz(deltaHz: number): void {
  if (!Number.isFinite(deltaHz) || deltaHz === 0) {
    if (deltaHz === 0) markOptimisticTune();
    return;
  }
  targetHz += deltaHz;
  const t = now();
  lastOptimisticTuneAtMs = t;
  lastTargetChangeAtMs = t;
  ensureRunning();
}

/** Stamp the optimistic-tune clock without moving the target (e.g. a POST
 *  flush of an already-applied pending value). */
export function markOptimisticTune(): void {
  lastOptimisticTuneAtMs = now();
}

/** Hard-set view == target == centerHz with no glide. The 'reset' path:
 *  first frame, width change, or no-overlap jump. Clears the refill hold. */
export function snapTo(centerHz: number, nextHzPerPixel?: number): void {
  targetHz = centerHz;
  viewHz = centerHz;
  if (nextHzPerPixel !== undefined && nextHzPerPixel > 0) hzPerPixel = nextHzPerPixel;
  initialized = true;
  lastTargetChangeAtMs = -Infinity;
  notify();
}

/**
 * Feed a server frame's center back in. Returns true when this frame was
 * recognised as an EXTERNAL tune (no recent operator gesture) — the caller
 * uses that to arm the refill hold for paths no gesture hook can see
 * (CAT/TCI, band buttons, typed entry, mode changes, arrow keys missed by
 * a stale build, …).
 */
export function reconcileFrame(centerHz: number, nextHzPerPixel: number): boolean {
  if (nextHzPerPixel > 0) hzPerPixel = nextHzPerPixel;
  if (!initialized) {
    snapTo(centerHz);
    return false;
  }
  const gapHz = Math.abs(centerHz - targetHz);
  if (gapHz <= Math.max(epsHz(), 1)) return false;
  const sinceOptimistic = now() - lastOptimisticTuneAtMs;
  if (sinceOptimistic < OPTIMISTIC_WINDOW_MS) {
    // Operator is tuning; frames lag the command. Ignore the mismatch.
    return false;
  }
  // External tune: glide to where the radio actually went.
  targetHz = centerHz;
  lastTargetChangeAtMs = now();
  ensureRunning();
  return true;
}

/** The animated center the surfaces should render against, in Hz. */
export function getViewCenterHz(): number {
  return viewHz;
}

/** False until the first frame/snap establishes a center. Surfaces render
 *  with zero offset until then. */
export function isInitialized(): boolean {
  return initialized;
}

// WDSP analyzer FFT size (WdspDspEngine.AnalyzerFftSize). The refill hold
// must outlast the FFT fill window — the span of old-frequency IQ still
// inside the analyzer after a retune — plus the EMA settle margin.
const ANALYZER_FFT_SIZE = 16384;
const HOLD_MARGIN_MS = 150;

/** Refill-hold duration for the current sample rate: 1.05 × FFT fill time
 *  + 150 ms (Thetis display.cs:6360 uses fftFillTime*1.05+2 frames; our
 *  margin also covers Phase 0's 100 ms fast-attack settle). Rate is clamped
 *  to the verified OpenHPSDR RX/DDC ladder through the G2 1.536 MHz ceiling
 *  so a transient bogus StateDto can't produce a multi-second hold
 *  (adversary: zoom-transition mis-derivation). */
export function refillHoldMsForSampleRate(sampleRateHz: number): number {
  const rate = Math.min(1_536_000, Math.max(48_000, sampleRateHz || 192_000));
  return (ANALYZER_FFT_SIZE / rate) * 1000 * 1.05 + HOLD_MARGIN_MS;
}

/** The commanded center (where the view is heading), in Hz. */
export function getTargetCenterHz(): number {
  return targetHz;
}

/** True while inside the post-retune refill hold: fresh server frames are
 *  still (partly) computed from pre-retune IQ and must not be adopted as
 *  trace content. holdMs is derived by the caller from the sample rate
 *  (FFT fill time) — see Panadapter.tsx. */
export function isWithinRefillHold(holdMs: number): boolean {
  return now() - lastTargetChangeAtMs < holdMs;
}

/** Milliseconds since the last operator-initiated tune. Used by the
 *  connection-store poll guard to suppress vfoHz rubber-banding from the
 *  1 Hz /api/state poll. */
export function msSinceOptimisticTune(): number {
  return now() - lastOptimisticTuneAtMs;
}

/** Subscribe to view-center motion. Fires once per tween tick (display
 *  rate while gliding; silent when parked). Returns the unsubscribe fn. */
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
  targetHz = 0;
  viewHz = 0;
  hzPerPixel = 0;
  lastOptimisticTuneAtMs = -Infinity;
  lastTargetChangeAtMs = -Infinity;
  lastTickMs = 0;
  listeners.clear();
}

export function _isLoopRunningForTest(): boolean {
  return rafHandle !== 0;
}
