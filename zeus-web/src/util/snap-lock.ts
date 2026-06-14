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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Self-correcting snap lock ("stay tuned"). After a snap-to-signal click, this
// singleton keeps the dial aligned to THAT signal as it drifts — re-measuring
// it every display frame and nudging the dial back onto its mode-tuning edge.
//
// The load-bearing design constraint (operator's words): "don't get confused
// with signals that are close to a weak signal we are trying to pull in." So the
// tracker never re-acquires "the nearest/strongest signal." It follows the ONE
// signal it locked, inside a capture window far narrower than the spacing to any
// neighbour (measureSnapLock), and clamps how far the anchor and dial may ever
// roam from the snap point. A louder neighbour OUTSIDE that window can never
// pull the dial; if the locked signal fades, the tracker HOLDS, then releases —
// it never jumps to whatever else is on the band.
//
// Everything here is gated behind Snap being on, an armed lock, RX (never during
// MOX/TUN), and an unchanged mode — and it disengages the instant the operator
// tunes by hand. Corrections are deadbanded, rate-limited, and step-limited, so
// the dial creeps rather than chases. Snap is display/UX surface — the engage
// model and these constants are the maintainer's call (CLAUDE.md).

import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { type DisplayState, registerFrameConsumer, subscribeFrames } from '../state/display-store';
import { measureSnapLock, type SnapLockMeasure, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useTxStore } from '../state/tx-store';
import * as viewCenter from '../state/view-center';

// Capture window for re-finding the locked signal each frame. Must stay well
// under the spacing to the neighbour you're trying not to grab.
const SNAP_LOCK_CAPTURE_HZ = 300;
// Hard bound on how far the dial/anchor may roam from the snap point — the
// ultimate "can't walk to the neighbour" guard, independent of the window.
const SNAP_LOCK_MAX_DRIFT_HZ = 500;
// Misalignment below this is ignored (no retune) — kills frame-to-frame chatter
// as the estimated edge breathes with the noise floor.
const SNAP_LOCK_DEADBAND_HZ = 40;
// Largest single correction, so the dial creeps onto the signal and can never
// lurch across a gap to an adjacent carrier.
const SNAP_LOCK_MAX_STEP_HZ = 80;
// Minimum spacing between committed retunes.
const SNAP_LOCK_COMMIT_INTERVAL_MS = 250;
// Consecutive frames with the locked signal in the noise before we let go.
const SNAP_LOCK_RELEASE_MISS_FRAMES = 25;
// A vfo change larger than this that the tracker didn't command = the operator
// (or another control) tuned → release. Above a fine-tune step, below a band hop.
const SNAP_LOCK_MANUAL_EPS_HZ = 60;

export type SnapLockState = {
  dialHz: number;
  bodyHz: number;
  originDialHz: number;
  originBodyHz: number;
  missFrames: number;
};

export type SnapLockCfg = {
  maxDriftHz: number;
  deadbandHz: number;
  maxStepHz: number;
  releaseMissFrames: number;
};

export type SnapLockStepResult = {
  dialHz: number;
  bodyHz: number;
  missFrames: number;
  /** Dial to retune to this frame, or null to hold (deadband / rate-limited / miss). */
  commitHz: number | null;
  /** True when the lock should be dropped (signal gone too long). */
  release: boolean;
};

const DEFAULT_CFG: SnapLockCfg = {
  maxDriftHz: SNAP_LOCK_MAX_DRIFT_HZ,
  deadbandHz: SNAP_LOCK_DEADBAND_HZ,
  maxStepHz: SNAP_LOCK_MAX_STEP_HZ,
  releaseMissFrames: SNAP_LOCK_RELEASE_MISS_FRAMES,
};

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

/** Pure decision core for one tracker frame. Given the lock state and this
 *  frame's measurement (or null when the signal wasn't found in the capture
 *  window), decide the next anchor, whether to retune, and whether to release.
 *  Side-effect free so the neighbour-safety guarantees are unit-testable. */
export function snapLockStep(
  state: SnapLockState,
  measure: SnapLockMeasure | null,
  cfg: SnapLockCfg = DEFAULT_CFG,
): SnapLockStepResult {
  if (measure === null) {
    const missFrames = state.missFrames + 1;
    return {
      dialHz: state.dialHz,
      bodyHz: state.bodyHz,
      missFrames,
      commitHz: null,
      release: missFrames >= cfg.releaseMissFrames,
    };
  }

  // Clamp the tracked anchor and target to ±maxDrift of the snap point so the
  // window can never creep onto a neighbour even over many frames.
  const bodyHz = clamp(measure.bodyHz, state.originBodyHz - cfg.maxDriftHz, state.originBodyHz + cfg.maxDriftHz);
  const target = clamp(measure.dialHz, state.originDialHz - cfg.maxDriftHz, state.originDialHz + cfg.maxDriftHz);
  const err = target - state.dialHz;
  if (Math.abs(err) < cfg.deadbandHz) {
    // Aligned enough — track the body silently, leave the dial put.
    return { dialHz: state.dialHz, bodyHz, missFrames: 0, commitHz: null, release: false };
  }
  const step = clamp(err, -cfg.maxStepHz, cfg.maxStepHz);
  const dialHz = state.dialHz + step;
  return { dialHz, bodyHz, missFrames: 0, commitHz: dialHz, release: false };
}

// ── Singleton controller ─────────────────────────────────────────────────────

let active = false;
let dialHz = 0;
let bodyHz = 0;
let originDialHz = 0;
let originBodyHz = 0;
let missFrames = 0;
let commandedHz = 0;
let lastCommitMs = 0;
let lockMode = useConnectionStore.getState().mode;
let unsubFrames: (() => void) | null = null;
let unsubVfo: (() => void) | null = null;
let releaseFrameConsumer: (() => void) | null = null;

function now(): number {
  return typeof performance !== 'undefined' ? performance.now() : 0;
}

/** True while a self-correcting lock is engaged. */
export function isSnapLocked(): boolean {
  return active;
}

/** Drop the lock and stop tracking. Leaves the dial exactly where it is — it
 *  never re-tunes on release. Idempotent. */
export function releaseSnapLock(): void {
  if (!active) return;
  active = false;
  unsubFrames?.();
  unsubFrames = null;
  unsubVfo?.();
  unsubVfo = null;
  releaseFrameConsumer?.();
  releaseFrameConsumer = null;
}

/** Engage (or re-point) the lock onto the signal a snap just tuned. `dialHz` is
 *  the committed dial; `anchorBodyHz` is a frequency inside the signal (the
 *  click point is ideal — it sits on the signal body). No-op unless Snap is on. */
export function armSnapLock(p: { dialHz: number; anchorBodyHz: number; mode: ReturnType<typeof useConnectionStore.getState>['mode'] }): void {
  if (!useSignalEnhanceStore.getState().snapEnabled) return;
  dialHz = p.dialHz;
  originDialHz = p.dialHz;
  bodyHz = p.anchorBodyHz;
  originBodyHz = p.anchorBodyHz;
  lockMode = p.mode;
  missFrames = 0;
  commandedHz = p.dialHz;
  lastCommitMs = now();
  if (!active) {
    active = true;
    releaseFrameConsumer = registerFrameConsumer();
    unsubFrames = subscribeFrames(onFrame);
    unsubVfo = useConnectionStore.subscribe(onVfoMaybeManual);
  }
}

function applyTune(hz: number): void {
  const conn = useConnectionStore.getState();
  const prevVfo = conn.vfoHz;
  // Mirror the click-tune commit: CTUN freezes the view (dial marker roams);
  // otherwise nudge the view-centre target by the delta so the dial stays put
  // and the display follows. Record the commanded value BEFORE the store write
  // so the manual-override watcher sees our own change as a match.
  if (conn.ctunEnabled) {
    viewCenter.markOptimisticTune();
  } else {
    viewCenter.nudgeTargetHz(hz - prevVfo);
  }
  commandedHz = hz;
  useConnectionStore.setState({ vfoHz: hz });
  setVfo(hz).catch(() => {});
}

function onFrame(s: DisplayState): void {
  if (!active) return;
  if (!useSignalEnhanceStore.getState().snapEnabled) {
    releaseSnapLock();
    return;
  }
  const tx = useTxStore.getState();
  if (tx.moxOn || tx.tunOn) {
    releaseSnapLock(); // never auto-tune during TX
    return;
  }
  const conn = useConnectionStore.getState();
  if (conn.status !== 'Connected' || conn.mode !== lockMode) {
    releaseSnapLock();
    return;
  }
  if (!s.panValid || !s.panDb || s.hzPerPixel <= 0) return;

  const measure = measureSnapLock(
    s.panDb,
    Number(s.centerHz),
    s.hzPerPixel,
    lockMode,
    bodyHz,
    SNAP_LOCK_CAPTURE_HZ,
  );
  const res = snapLockStep({ dialHz, bodyHz, originDialHz, originBodyHz, missFrames }, measure);
  missFrames = res.missFrames;
  bodyHz = res.bodyHz;
  if (res.release) {
    releaseSnapLock();
    return;
  }
  if (res.commitHz !== null && now() - lastCommitMs >= SNAP_LOCK_COMMIT_INTERVAL_MS) {
    applyTune(res.commitHz);
    dialHz = res.commitHz;
    lastCommitMs = now();
  }
}

function onVfoMaybeManual(s: { vfoHz: number }, prev: { vfoHz: number }): void {
  if (!active) return;
  if (s.vfoHz === prev.vfoHz) return;
  // A vfo move we didn't command = operator/keyboard/band/typed tune → let go.
  if (Math.abs(s.vfoHz - commandedHz) > SNAP_LOCK_MANUAL_EPS_HZ) releaseSnapLock();
}

/** Test-only: reset all controller state without touching subscriptions. */
export function _resetSnapLockForTest(): void {
  releaseSnapLock();
  dialHz = bodyHz = originDialHz = originBodyHz = missFrames = commandedHz = lastCommitMs = 0;
}
