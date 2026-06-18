// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// 60 Hz display-side interpolation — a pure presentation layer that glides
// HOW a value is DRAWN, never WHAT value is read. It sits on top of the
// already-published (~30 Hz) value from useBallisticReading and springs the
// drawn position toward it on the shared draw-bus. It does NOT touch the
// value pipeline (ballistics.ts, peakHoldStep, advancePeakHold,
// PUBLISH_INTERVAL_MS). PS / calibration stay byte-identical because at
// steady state the drawn value === the target exactly.
//
// SPRING — semi-implicit (symplectic Euler) critically-damped, per frame:
//     accel = -k*(drawn - target) - c*vel
//     vel  += accel*dt
//     drawn += vel*dt
//   • Stiffness k≈170 → settle ~80-120 ms at 60 Hz.
//   • Damping ratio: FALLS use 1.0 (critical, smooth decay, no overshoot);
//     RISES use ~0.86 (slight under-damp → the requested subtle
//     overshoot-settle / momentum). c = 2*ratio*sqrt(k).
//   • dt clamped to 0.05 s by the draw-bus.
//   • PARK: when |drawn-target| < EPS and |vel| small, snap + go idle so the
//     subscriber stops asking the bus to step it while steady.
//
// PEAK IS NEVER GLIDED. The peak fraction/tick passes through the raw latched
// value untouched and latches on the same frame; callers wire peak straight
// from the raw pkRef/peak, never from this hook's output.

import { useEffect, useRef, type RefObject } from 'react';
import { subscribe } from './drawBus';

// Presentation-only timing constants (NOT tokens — these are motion, not
// colour). Named here so a future feel tweak lives in one place.
const STIFFNESS = 170; // k — settle in ~80-120 ms at 60 Hz
const RISE_DAMPING = 0.86; // < 1 → subtle overshoot-settle on rises
const FALL_DAMPING = 1.0; // critical → smooth decay, no overshoot
const EPS_PARK = 5e-4; // park when within this of target …
const VEL_PARK = 5e-3; // … and velocity below this

function prefersReducedMotion(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches
  );
}

export interface GlideOptions {
  /** Host element for the draw-bus off-screen gate (the widget root). */
  hostRef?: RefObject<Element | null>;
  /**
   * Imperative writer called every frame the drawn value moves, with the
   * fresh 0..1 drawn fraction. Preferred path: write a transform straight to
   * an element ref (scaleX / translateX for bars, rotate for the needle) so
   * NO React setState runs in the 60 Hz path and reconciliation stays at the
   * 30 Hz value cadence. If omitted the hook still glides internally and
   * `read()` returns the live drawn value (for callers that sample at their
   * own render cadence).
   */
  onDraw?: (drawn: number) => void;
}

export interface GlideHandle {
  /** Current drawn fraction (0..1). Cheap synchronous read for callers that
   *  compute geometry off the drawn value at their own paint cadence. */
  read: () => number;
}

/**
 * Glide a calibrated target fraction (or any already-normalised 0..1 scalar
 * — angle/N work too) toward its latest value on the shared 60 Hz bus.
 *
 * The target is passed every render; the hook stashes it in a ref so the bus
 * step always springs toward the freshest published value without
 * re-subscribing. prefers-reduced-motion forces critical damping both
 * directions (no overshoot) so motion is still smooth but never bounces.
 */
export function useGlidedFraction(target: number, opts: GlideOptions = {}): GlideHandle {
  const targetRef = useRef(target);
  targetRef.current = Number.isFinite(target) ? target : 0;

  const drawnRef = useRef(targetRef.current);
  const velRef = useRef(0);
  const idleRef = useRef(false);

  const onDrawRef = useRef(opts.onDraw);
  onDrawRef.current = opts.onDraw;

  const hostRef = opts.hostRef;

  useEffect(() => {
    const reduced = prefersReducedMotion();

    const step = (dt: number) => {
      const target = targetRef.current;
      let drawn = drawnRef.current;
      let vel = velRef.current;

      const delta = drawn - target;

      // Park check — snap exact and stop stepping while steady so a row of
      // idle meters costs nothing. The first non-trivial target change wakes
      // it again (see the wake below).
      if (Math.abs(delta) < EPS_PARK && Math.abs(vel) < VEL_PARK) {
        if (!idleRef.current) {
          drawnRef.current = target; // byte-exact at rest → zero draw drift
          velRef.current = 0;
          idleRef.current = true;
          onDrawRef.current?.(target);
        }
        return;
      }
      idleRef.current = false;

      // Rises under-damp slightly (momentum/overshoot); falls critically
      // damp (smooth decay). prefers-reduced-motion forces critical both ways.
      const rising = target > drawn;
      const ratio = reduced ? 1.0 : rising ? RISE_DAMPING : FALL_DAMPING;
      const c = 2 * ratio * Math.sqrt(STIFFNESS);

      const accel = -STIFFNESS * delta - c * vel;
      vel += accel * dt;
      drawn += vel * dt;

      drawnRef.current = drawn;
      velRef.current = vel;
      onDrawRef.current?.(drawn);
    };

    // Wake the spring whenever React re-renders with a fresh target: the
    // park-snap above marks idle, but a new published value must restart
    // stepping. A 60 Hz subscriber that's parked simply returns early each
    // frame (nearly free); we keep it subscribed so it picks up the next
    // change instantly without a re-subscribe round-trip.
    const unsub = subscribe(step, hostRef?.current ?? null);
    return unsub;
    // host element identity is stable for the widget's life; target changes
    // flow through targetRef without re-subscribing.
  }, [hostRef]);

  // Re-arm out of park on every render where the target moved meaningfully.
  if (idleRef.current && Math.abs(drawnRef.current - targetRef.current) >= EPS_PARK) {
    idleRef.current = false;
  }

  return {
    read: () => drawnRef.current,
  };
}
