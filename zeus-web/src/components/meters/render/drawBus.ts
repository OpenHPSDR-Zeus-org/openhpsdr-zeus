// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared 60 Hz draw-bus for the meter cluster — ONE module-level
// requestAnimationFrame that drives every presentation-layer glide for the
// whole meter set. There is never a timer or rAF per meter: each meter
// subscribes a per-frame step function; the bus runs rAF only while at least
// one subscriber is registered and self-cancels when the set empties.
//
// DISPLAY-ONLY. This bus interpolates how a value is DRAWN — it does not
// read, smooth, or publish any meter VALUE. The value pipeline
// (useBallisticReading / ballistics.ts / peak-hold) is untouched; the glide
// hooks that subscribe here consume the already-published value and only
// decide the drawn position between updates.
//
// GATING — idle meters and hidden tabs cost zero frames, so the panadapter
// is unaffected:
//   • The whole tick is skipped while document.hidden.
//   • Each subscriber may register its host element; an IntersectionObserver
//     (same pattern as useBallisticReading.ts:218-238 / AnalogMeterPanel:
//     334-346) marks it off-screen and the bus skips stepping it. When every
//     live subscriber is off-screen the rAF still parks (no visible work).

/** Per-frame step. `dt` is seconds since the last tick, clamped to 0.05 s
 *  (matches the wake-protection clamps in the value pipeline) so a tab wake
 *  or a long frame never yanks the spring. */
export type DrawStep = (dt: number) => void;

interface Subscriber {
  step: DrawStep;
  /** Host element for off-screen gating; null → always considered visible. */
  host: Element | null;
  /** Last IntersectionObserver verdict — true until told otherwise. */
  visible: boolean;
}

const subscribers = new Set<Subscriber>();
let raf = 0;
let lastMs = 0;
let observer: IntersectionObserver | null = null;
// Map from observed element → subscriber so the IO callback can flip its flag.
const byElement = new WeakMap<Element, Subscriber>();

const DT_CLAMP_S = 0.05;

function pageHidden(): boolean {
  return typeof document !== 'undefined' && document.hidden;
}

function ensureObserver(): IntersectionObserver | null {
  if (typeof IntersectionObserver === 'undefined') return null;
  if (observer) return observer;
  observer = new IntersectionObserver(
    (entries) => {
      for (const e of entries) {
        const sub = byElement.get(e.target);
        if (sub) sub.visible = e.isIntersecting;
      }
    },
    { threshold: 0 },
  );
  return observer;
}

function tick(now: number) {
  raf = 0;
  if (subscribers.size === 0) return;

  // Skip the whole tick on a hidden tab — but keep the loop alive so we
  // resume instantly on un-hide. (The dt anchor is reset on the first
  // visible frame via the lastMs==0 seed below.)
  if (pageHidden()) {
    lastMs = 0;
    raf = requestAnimationFrame(tick);
    return;
  }

  const dt = lastMs === 0 ? 0 : Math.min(DT_CLAMP_S, Math.max(0, (now - lastMs) / 1000));
  lastMs = now;

  for (const sub of subscribers) {
    if (sub.host && !sub.visible) continue;
    sub.step(dt);
  }

  if (subscribers.size > 0) raf = requestAnimationFrame(tick);
}

function start() {
  if (raf === 0 && subscribers.size > 0) {
    lastMs = 0; // reseed dt anchor so the first frame doesn't count the gap
    raf = requestAnimationFrame(tick);
  }
}

/**
 * Register a per-frame step. Returns an unsubscribe fn. Pass `host` to opt
 * the subscriber into off-screen gating (the bus skips stepping it while its
 * element is not intersecting the viewport).
 */
export function subscribe(step: DrawStep, host?: Element | null): () => void {
  const sub: Subscriber = { step, host: host ?? null, visible: true };
  subscribers.add(sub);

  if (sub.host) {
    const io = ensureObserver();
    if (io) {
      byElement.set(sub.host, sub);
      io.observe(sub.host);
    }
  }

  start();

  return () => {
    subscribers.delete(sub);
    if (sub.host && observer) {
      observer.unobserve(sub.host);
      byElement.delete(sub.host);
    }
    if (subscribers.size === 0 && raf !== 0) {
      cancelAnimationFrame(raf);
      raf = 0;
      lastMs = 0;
    }
  };
}
