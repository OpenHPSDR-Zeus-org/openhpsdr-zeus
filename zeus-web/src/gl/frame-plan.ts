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

// Shared per-frame shift planner (issue #597 Phase 1).
//
// Before this module, Panadapter.tsx and createWfRenderer each kept their
// own copy of the planner tracker (lastCenterHz / lastHzPerPixel /
// lastWidth). The two copies were gated on DIFFERENT validity flags
// (panValid vs wfValid), so when the flags disagreed across a retune tick
// the two surfaces applied the same shift on different frames — a visible
// one-frame horizontal disagreement between trace and waterfall.
//
// This module computes ONE decision per frame seq from the frame's header
// fields (which are present regardless of pan/wf validity) and hands the
// identical decision to every consumer. The tracker advances on every seq —
// including frames whose payloads are invalid — so neither surface can
// drift against the other.

import { planWaterfallUpdate, type WfShiftDecision } from './wf-shift';

type FramePlanTracker = {
  plannedSeq: number;
  plannedDecision: WfShiftDecision;
  lastCenterHz: bigint | null;
  lastHzPerPixel: number;
  lastWidth: number;
};

const DEFAULT_PLAN_KEY = 'default';
const trackers = new Map<string, FramePlanTracker>();

export type FramePlanInput = {
  seq: number;
  centerHz: bigint;
  hzPerPixel: number;
  width: number;
  planKey?: string;
};

function createTracker(): FramePlanTracker {
  return {
    plannedSeq: -1,
    plannedDecision: { kind: 'reset', reason: 'first' },
    lastCenterHz: null,
    lastHzPerPixel: 0,
    lastWidth: 0,
  };
}

function trackerFor(planKey: string): FramePlanTracker {
  let tracker = trackers.get(planKey);
  if (!tracker) {
    tracker = createTracker();
    trackers.set(planKey, tracker);
  }
  return tracker;
}

/**
 * Decision for frame `seq`. Memoized: the first caller for a given seq
 * computes and advances the tracker; subsequent callers (same seq) get the
 * identical decision back. Callers MUST invoke this for every frame they
 * observe, in seq order — both spectrum components already subscribe to
 * every store update, so this holds by construction.
 */
export function planForFrame(i: FramePlanInput): WfShiftDecision {
  const tracker = trackerFor(i.planKey ?? DEFAULT_PLAN_KEY);
  if (i.seq === tracker.plannedSeq) return tracker.plannedDecision;
  const decision = planWaterfallUpdate({
    lastCenterHz: tracker.lastCenterHz,
    lastHzPerPixel: tracker.lastHzPerPixel,
    lastWidth: tracker.lastWidth,
    nextCenterHz: i.centerHz,
    nextHzPerPixel: i.hzPerPixel,
    nextWidth: i.width,
  });
  switch (decision.kind) {
    case 'reset':
      tracker.lastCenterHz = i.centerHz;
      tracker.lastHzPerPixel = i.hzPerPixel;
      tracker.lastWidth = i.width;
      break;
    case 'push':
      // lastCenterHz intentionally unchanged so sub-pixel retunes accumulate
      // (same convention as the original per-component trackers).
      break;
    case 'shift':
      tracker.lastCenterHz = decision.residualCenterHz;
      break;
    case 'rescale':
      tracker.lastCenterHz = i.centerHz;
      tracker.lastHzPerPixel = i.hzPerPixel;
      tracker.lastWidth = i.width;
      break;
  }
  tracker.plannedSeq = i.seq;
  tracker.plannedDecision = decision;
  return decision;
}

/** The center frequency the planner currently believes the on-screen data
 *  is anchored at (post-shift residual). Null before the first frame. */
export function plannedDataCenterHz(planKey = DEFAULT_PLAN_KEY): bigint | null {
  return trackers.get(planKey)?.lastCenterHz ?? null;
}

/** Forget all tracker state so the next observed frame is planned as a clean
 *  'reset'. Used on reconnect AND on WebGL context restore (#629): after the
 *  waterfall's GPU history textures are lost and the renderer is rebuilt, the
 *  next frame must re-seed them — but under unchanged geometry the planner
 *  would otherwise emit 'push'/'shift', never 'reset'. Resetting the shared
 *  tracker forces the seeding 'reset'. The panadapter simply re-snaps its
 *  surviving CPU-side anchor on the same frame (a single snap, no glide). */
export function resetFramePlan(planKey?: string): void {
  if (planKey === undefined) {
    trackers.clear();
    return;
  }
  trackers.set(planKey, createTracker());
}

/** Test alias — kept so existing specs import the same behaviour. */
export const _resetFramePlanForTest = resetFramePlan;
