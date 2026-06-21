// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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

// Planner for waterfall horizontal shift on VFO change (doc 08 §5). Pure
// logic so it can be unit-tested without a WebGL context.
//
// Input is the "last-seen" frame geometry (width, hzPerPixel, centerHz) and
// the incoming frame's same fields. Output tells the renderer what to do:
//   - reset: wipe history (first frame, width changed, or the LO moved far
//     enough that the whole visible span is new data)
//   - push:  no translation needed, upload the new row normally
//   - shift: ping-pong horizontal shift by `shiftPx` columns; suppress
//     this tick's row blit and remember the residual sub-pixel delta so
//     subsequent fine retunes accumulate instead of being dropped
//   - rescale: hzPerPixel changed but the old/new spans overlap, so resample
//     the existing history around the new center instead of throwing it away
//
// Sign convention: shiftPx = round((oldCenter − newCenter) / hzPerPixel).
// The server emits `wfDb` with low freq on the left / high
// freq on the right (see DspPipelineService.Tick — unconditional
// Array.Reverse), so a positive shiftPx means columns slide right.

export type WfShiftInput = {
  lastCenterHz: bigint | null;
  lastHzPerPixel: number;
  lastWidth: number;
  nextCenterHz: bigint;
  nextHzPerPixel: number;
  nextWidth: number;
};

export type WfShiftDecision =
  | { kind: 'reset'; reason: 'first' | 'width' | 'span' }
  | { kind: 'push' }
  | { kind: 'shift'; shiftPx: number; residualCenterHz: bigint }
  | { kind: 'rescale'; srcXScale: number; srcCenterOffsetUv: number };

export function planWaterfallUpdate(i: WfShiftInput): WfShiftDecision {
  if (i.lastWidth === 0 || i.lastCenterHz === null) {
    return { kind: 'reset', reason: 'first' };
  }
  if (i.lastWidth !== i.nextWidth) return { kind: 'reset', reason: 'width' };
  if (i.lastHzPerPixel !== i.nextHzPerPixel) {
    const oldSpanHz = i.lastWidth * i.lastHzPerPixel;
    const newSpanHz = i.nextWidth * i.nextHzPerPixel;
    const oldCenterHz = Number(i.lastCenterHz);
    const newCenterHz = Number(i.nextCenterHz);
    const oldLo = oldCenterHz - oldSpanHz / 2;
    const oldHi = oldCenterHz + oldSpanHz / 2;
    const newLo = newCenterHz - newSpanHz / 2;
    const newHi = newCenterHz + newSpanHz / 2;
    if (Math.max(oldLo, newLo) >= Math.min(oldHi, newHi)) {
      return { kind: 'reset', reason: 'span' };
    }
    return {
      kind: 'rescale',
      srcXScale: i.nextHzPerPixel / i.lastHzPerPixel,
      srcCenterOffsetUv: (newCenterHz - oldCenterHz) / oldSpanHz,
    };
  }
  const deltaHz = Number(i.lastCenterHz - i.nextCenterHz);
  const shiftPx = Math.round(deltaHz / i.nextHzPerPixel);
  if (shiftPx === 0) return { kind: 'push' };
  if (Math.abs(shiftPx) >= i.nextWidth) {
    return { kind: 'reset', reason: 'span' };
  }
  // Apply the integer-pixel shift and roll the sub-pixel remainder forward
  // so a sequence of fine retunes doesn't drop into the rounding gap.
  const appliedHz = BigInt(Math.round(shiftPx * i.nextHzPerPixel));
  const residualCenterHz = i.lastCenterHz - appliedHz;
  return { kind: 'shift', shiftPx, residualCenterHz };
}
