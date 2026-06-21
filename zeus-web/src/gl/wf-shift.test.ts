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

import { describe, expect, it } from 'vitest';
import { planWaterfallUpdate } from './wf-shift';

const base = {
  lastCenterHz: 14_200_000n,
  lastHzPerPixel: 100,
  lastWidth: 1024,
  nextCenterHz: 14_200_000n,
  nextHzPerPixel: 100,
  nextWidth: 1024,
};

describe('planWaterfallUpdate', () => {
  it('resets on first frame (no prior center)', () => {
    expect(
      planWaterfallUpdate({ ...base, lastCenterHz: null, lastWidth: 0 }),
    ).toEqual({ kind: 'reset', reason: 'first' });
  });

  it('resets when width changes', () => {
    expect(
      planWaterfallUpdate({ ...base, nextWidth: 2048 }),
    ).toEqual({ kind: 'reset', reason: 'width' });
  });

  it('rescales when hzPerPixel changes and spans overlap', () => {
    expect(
      planWaterfallUpdate({ ...base, nextHzPerPixel: 50 }),
    ).toEqual({
      kind: 'rescale',
      srcXScale: 0.5,
      srcCenterOffsetUv: 0,
    });
  });

  it('includes center offset when rescaling during a tune', () => {
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: 14_201_000n,
      nextHzPerPixel: 50,
    });
    expect(d).toMatchObject({
      kind: 'rescale',
      srcXScale: 0.5,
    });
    expect(d.kind === 'rescale' ? d.srcCenterOffsetUv : 0).toBeCloseTo(1000 / (1024 * 100));
  });

  it('resets when a zoomed span no longer overlaps old history', () => {
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: 14_400_000n,
      nextHzPerPixel: 50,
    });
    expect(d).toEqual({ kind: 'reset', reason: 'span' });
  });

  it('pushes a row when center is unchanged', () => {
    expect(planWaterfallUpdate(base)).toEqual({ kind: 'push' });
  });

  it('pushes when sub-pixel retune does not cross a column', () => {
    // 50 Hz at 100 Hz/px rounds to 0 — carrier should sit still, no shift.
    // lastCenterHz must stay put so the next 50 Hz step accumulates to a
    // full pixel. The renderer enforces that by only updating lastCenterHz
    // on reset/shift (not on push), so here we just assert the decision.
    expect(
      planWaterfallUpdate({ ...base, nextCenterHz: 14_200_050n }),
    ).toEqual({ kind: 'push' });
  });

  it('shifts right (+shiftPx) when tuning down (oldCenter > newCenter)', () => {
    // 14.200 → 14.199 MHz: 1000 Hz / 100 Hz/px = 10 columns right.
    // Integer-pixel retune — residual matches the new center exactly.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_199_000n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: 10,
      residualCenterHz: 14_199_000n,
    });
  });

  it('shifts left (-shiftPx) when tuning up (newCenter > oldCenter)', () => {
    // 14.200 → 14.201 MHz: −1000 Hz / 100 Hz/px = −10 columns.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_201_000n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: -10,
      residualCenterHz: 14_201_000n,
    });
  });

  it('preserves sub-pixel residual so fine retunes accumulate', () => {
    // A 150 Hz retune at 100 Hz/px rounds to 1 px, leaves a 50 Hz residual.
    // The planner reports the residual back as the new lastCenterHz so the
    // NEXT retune sees a larger effective delta — a second 150 Hz step
    // will shift another 2 px (not 1), catching up the missed column.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_199_850n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: 2,
      residualCenterHz: 14_200_000n - 200n, // 2 * 100 applied
    });
  });

  it('resets when |shift| >= width (retune larger than the visible span)', () => {
    // 100 kHz retune at 100 Hz/px = 1000 columns; width=1024 → reset.
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: base.lastCenterHz - 200_000n,
    });
    expect(d).toEqual({ kind: 'reset', reason: 'span' });
  });

  it('treats |shift| exactly equal to width as a reset (nothing to keep)', () => {
    const w = base.lastWidth;
    const hz = BigInt(w * base.lastHzPerPixel);
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: base.lastCenterHz - hz,
    });
    expect(d).toEqual({ kind: 'reset', reason: 'span' });
  });
});
