// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared "stream during drag, commit on release" helper for slider inputs.
//
// Background (issue zeus-5k0): every slider in Zeus should feel live —
// the operator hears / sees the change as the thumb moves, not just on
// mouseUp. Previously some sliders (AGC-T, S-ATT) only POSTed on release;
// others (AF, DRV, TUN, MIC, LVLR) debounced 100 ms which is noticeable
// on a fast drag.
//
// This hook standardises the contract:
//   * Intermediate values are coalesced via requestAnimationFrame so we
//     post at most once per paint (~16 ms at 60 Hz) — feels instant to
//     the operator while preventing endpoint flooding from a wild drag.
//   * In-flight requests are aborted when a newer value supersedes them
//     so the backend always sees the latest intent (and the slider isn't
//     yanked back by a stale echo).
//   * flush() commits the latest pending value immediately — call from
//     pointerUp / mouseUp / touchEnd / keyUp / blur so the final value is
//     guaranteed to land regardless of throttle alignment.
//
// Optimistic store updates remain the caller's responsibility (every
// existing slider does this in its own onChange — we don't want to bake
// the store shape into a generic hook).

import { useCallback, useEffect, useRef } from 'react';

/**
 * Callback shape every slider already has: take a value, return a promise
 * that resolves when the server has acknowledged. The hook passes an
 * AbortSignal so the caller can plumb it through fetch().
 */
export type LiveSliderSender<T> = (value: T, signal: AbortSignal) => Promise<void>;

export interface UseLiveSliderOptions<T> {
  /**
   * Send the value to the backend. The hook calls this with the latest
   * pending value at most once per animation frame; older calls are
   * superseded (their AbortSignals are aborted).
   */
  send: LiveSliderSender<T>;

  /**
   * Equality check — by default `Object.is`. Override for slider values
   * that are objects (rare; most are numbers).
   */
  equals?: (a: T, b: T) => boolean;
}

export interface UseLiveSliderApi<T> {
  /**
   * Queue a value to be sent on the next animation frame, coalescing
   * with any later push() calls that arrive in the same frame.
   */
  push: (value: T) => void;
  /**
   * Send the latest pending value immediately (no rAF wait). Safe to
   * call from pointerUp / mouseUp / touchEnd / keyUp / blur handlers
   * even if no value is pending — it no-ops in that case.
   */
  flush: () => void;
}

/**
 * Stream slider values during drag, commit on release.
 *
 * Cleanup (cancel rAF + abort in-flight) happens automatically on unmount.
 */
export function useLiveSlider<T>(opts: UseLiveSliderOptions<T>): UseLiveSliderApi<T> {
  const { send, equals = Object.is } = opts;

  // Keep the latest send in a ref so callers don't have to memoise it —
  // we only ever read it inside the rAF tick, where stale closures would
  // otherwise be a footgun (an old `send` would post to the wrong store).
  const sendRef = useRef(send);
  sendRef.current = send;
  const equalsRef = useRef(equals);
  equalsRef.current = equals;

  // Pending value waiting for the next rAF tick. `hasPending` is needed
  // (vs. just checking pendingRef.current) because T might legitimately
  // be 0 / null / undefined and we still want to fire.
  const pendingRef = useRef<T | undefined>(undefined);
  const hasPendingRef = useRef(false);

  // rAF handle so we can cancel on flush / unmount.
  const rafRef = useRef<number | null>(null);

  // Last value actually dispatched to send() — used to short-circuit
  // duplicate posts (operator drags away and back to the same value).
  const lastDispatchedRef = useRef<T | undefined>(undefined);
  const hasDispatchedRef = useRef(false);

  // In-flight AbortController — aborted when a newer value comes in.
  const inflightRef = useRef<AbortController | null>(null);

  const dispatch = useCallback((value: T) => {
    if (hasDispatchedRef.current && equalsRef.current(value, lastDispatchedRef.current as T)) {
      return;
    }
    lastDispatchedRef.current = value;
    hasDispatchedRef.current = true;
    inflightRef.current?.abort();
    const ac = new AbortController();
    inflightRef.current = ac;
    // Fire-and-forget — error handling is the sender's responsibility
    // (every existing slider already does rollback / silent ignore on
    // AbortError, and we don't want to swallow those decisions here).
    void sendRef.current(value, ac.signal).catch(() => {
      /* sender owns error handling */
    });
  }, []);

  const push = useCallback((value: T) => {
    pendingRef.current = value;
    hasPendingRef.current = true;
    if (rafRef.current != null) return;
    // Use rAF when available, fall back to setTimeout(0) for non-browser
    // (jsdom) test environments where rAF is stubbed or absent.
    const schedule: (cb: () => void) => number =
      typeof window !== 'undefined' && typeof window.requestAnimationFrame === 'function'
        ? (cb) => window.requestAnimationFrame(cb)
        : (cb) => setTimeout(cb, 0) as unknown as number;
    rafRef.current = schedule(() => {
      rafRef.current = null;
      if (!hasPendingRef.current) return;
      const v = pendingRef.current as T;
      hasPendingRef.current = false;
      pendingRef.current = undefined;
      dispatch(v);
    });
  }, [dispatch]);

  const flush = useCallback(() => {
    if (rafRef.current != null) {
      if (typeof window !== 'undefined' && typeof window.cancelAnimationFrame === 'function') {
        window.cancelAnimationFrame(rafRef.current);
      } else {
        clearTimeout(rafRef.current as unknown as ReturnType<typeof setTimeout>);
      }
      rafRef.current = null;
    }
    if (!hasPendingRef.current) return;
    const v = pendingRef.current as T;
    hasPendingRef.current = false;
    pendingRef.current = undefined;
    dispatch(v);
  }, [dispatch]);

  useEffect(() => () => {
    if (rafRef.current != null) {
      if (typeof window !== 'undefined' && typeof window.cancelAnimationFrame === 'function') {
        window.cancelAnimationFrame(rafRef.current);
      } else {
        clearTimeout(rafRef.current as unknown as ReturnType<typeof setTimeout>);
      }
      rafRef.current = null;
    }
    inflightRef.current?.abort();
  }, []);

  return { push, flush };
}
