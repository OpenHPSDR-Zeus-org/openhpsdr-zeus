// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// Distributed under GPL-2.0-or-later. See LICENSE at the repository root.

// Shared draw bus — coalesces panadapter + waterfall (and any other canvas
// that opts in) into a single requestAnimationFrame wakeup per frame.
//
// Background: each canvas component used to call its own
//   if (rafHandle === 0) rafHandle = requestAnimationFrame(redraw)
// from inside a `useDisplayStore.subscribe` handler. With panadapter and
// waterfall both reacting to the same `lastSeq` update, every server
// spectrum push (~25 Hz) produced two independent rAF wakeups. The renderer
// did the same total work either way, but two callbacks meant two scheduler
// turns and two paint-commit fences per frame.
//
// The bus collapses that to one. Components register a redraw callback via
// `requestDrawBusFrame(cb)`; the first request in a frame schedules a
// single rAF, which fans out to every pending callback in registration
// order. Re-requesting the same callback before the rAF fires is a no-op
// (Set-deduped), matching the existing `if (rafHandle === 0)` gate.
//
// On the rAF tick we snapshot and clear the pending set first, so a
// callback that re-requests a redraw (e.g. on a new store update mid-flush)
// lands on the *next* rAF, not this one. This preserves the natural
// back-pressure of the previous per-component pattern.
//
// `cancelDrawBusFrame(cb)` is the unmount safety hatch — components must
// call it in their effect cleanup so a stale closure doesn't fire after
// the WebGL context has been disposed.

type DrawCallback = () => void;

const pending = new Set<DrawCallback>();
let rafHandle = 0;

const flush = () => {
  rafHandle = 0;
  // Snapshot before invoking so a callback that re-arms itself lands on
  // the next frame rather than this one. Iteration order is insertion
  // order on Set, which keeps the panadapter-then-waterfall ordering
  // stable across frames.
  const cbs = Array.from(pending);
  pending.clear();
  for (const cb of cbs) {
    try {
      cb();
    } catch (err) {
      // One bad callback shouldn't abort the rest. Log and keep flushing.

      console.error('draw-bus callback threw', err);
    }
  }
};

/**
 * Schedule `cb` to run on the next animation frame. Idempotent — repeated
 * requests for the same callback within a frame are coalesced. Multiple
 * distinct callbacks share a single rAF wakeup.
 */
export function requestDrawBusFrame(cb: DrawCallback): void {
  pending.add(cb);
  if (rafHandle === 0) {
    rafHandle = requestAnimationFrame(flush);
  }
}

/**
 * Drop a pending request for `cb`. Components MUST call this in their
 * unmount cleanup so a stale callback (closed over a disposed WebGL
 * context) doesn't fire after teardown.
 */
export function cancelDrawBusFrame(cb: DrawCallback): void {
  pending.delete(cb);
}

/**
 * Test helper — reset the bus between tests. Not exported from a barrel.
 */
export function _resetDrawBusForTest(): void {
  pending.clear();
  if (rafHandle !== 0) {
    cancelAnimationFrame(rafHandle);
    rafHandle = 0;
  }
}
