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

// Waterfall colour palettes. All three are resampled into 256-entry RGBA
// LUTs by linear interpolation between anchor stops; the GL sampler does the
// rest at draw time.
//
// - Blue: classic SDR palette (black → dark blue → cyan → green → yellow →
//   red → white). Keeps the noise-floor visually quiet and highlights peaks crisp.
// - Inferno: matplotlib-style dark-to-red-to-yellow. Perceptually uniform,
//   high dynamic range, matches the Thetis default.
// - Viridis: matplotlib perceptual default (purple → teal → green → yellow).
//   Colour-blind-safe and preserves ordering under greyscale reduction.
//
// Anchor colours for inferno/viridis are sampled from matplotlib's
// published RGB tables (BSD-compatible) at 5 stops each — dense enough that
// the 256-entry linear resample is visually indistinguishable from the
// continuous form for our use case.

export type ColormapId = 'blue' | 'inferno' | 'viridis';
export type RenderColormapId = ColormapId | 'pop';

export type ColormapSpec = {
  id: ColormapId;
  label: string;
};

export const COLORMAPS: readonly ColormapSpec[] = [
  { id: 'blue', label: 'Blue' },
  { id: 'inferno', label: 'Inferno' },
  { id: 'viridis', label: 'Viridis' },
];

type Anchor = [number, [number, number, number]];

// Low-end anchors are stretched: roughly the bottom ~35% of each LUT sits at
// or near black so a noisy band doesn't flood the display with mid-band
// colour. The signal range is compressed into the upper ~65% — peaks still
// reach the same top colour, but the noise floor reads dark.
const BLUE_ANCHORS: Anchor[] = [
  [0.0, [0, 0, 0]],
  [0.35, [0, 0, 0]],
  [0.45, [0, 0, 128]],
  [0.55, [0, 0, 255]],
  [0.65, [0, 255, 255]],
  [0.75, [0, 255, 0]],
  [0.85, [255, 255, 0]],
  [0.93, [255, 0, 0]],
  [1.0, [255, 255, 255]],
];

const INFERNO_ANCHORS: Anchor[] = [
  [0.0, [0, 0, 4]],
  [0.35, [0, 0, 4]],
  [0.5, [40, 11, 84]],
  [0.6, [101, 21, 110]],
  [0.72, [190, 55, 82]],
  [0.85, [236, 121, 35]],
  [0.95, [252, 200, 45]],
  [1.0, [252, 255, 164]],
];

const VIRIDIS_ANCHORS: Anchor[] = [
  [0.0, [22, 0, 27]],
  [0.35, [22, 0, 27]],
  [0.5, [59, 82, 139]],
  [0.65, [33, 144, 141]],
  [0.85, [94, 201, 98]],
  [1.0, [253, 231, 37]],
];

// Signal Pop palette — Christian Suarez (N9WAR)'s retune from his rx2 engine: the
// four lower anchors sit lower than develop's, so weak signals and floor
// structure light up earlier, giving the 3D relief more to shade. Adopted with
// the new waterfall engine (PR #655); this intentionally diverges from
// develop's byte-identical Signal Pop palette.
const POP_ANCHORS: Anchor[] = [
  [0.0, [0, 0, 0]],
  [0.12, [0, 0, 0]],
  [0.26, [2, 18, 34]],
  [0.42, [0, 74, 122]],
  [0.56, [0, 178, 220]],
  [0.70, [42, 236, 200]],
  [0.82, [232, 214, 82]],
  [0.92, [255, 118, 40]],
  [1.0, [255, 248, 214]],
];

function sampleAnchors(anchors: Anchor[], t: number): [number, number, number] {
  let lo = anchors[0]!;
  let hi = anchors[anchors.length - 1]!;
  for (let k = 0; k < anchors.length - 1; k++) {
    const a = anchors[k]!;
    const b = anchors[k + 1]!;
    if (t >= a[0] && t <= b[0]) {
      lo = a;
      hi = b;
      break;
    }
  }
  const span = hi[0] - lo[0];
  const f = span > 0 ? (t - lo[0]) / span : 0;
  return [
    Math.round(lo[1][0] + (hi[1][0] - lo[1][0]) * f),
    Math.round(lo[1][1] + (hi[1][1] - lo[1][1]) * f),
    Math.round(lo[1][2] + (hi[1][2] - lo[1][2]) * f),
  ];
}

function buildLut(anchors: Anchor[]): Uint8Array {
  const buf = new Uint8Array(256 * 4);
  for (let i = 0; i < 256; i++) {
    const [r, g, b] = sampleAnchors(anchors, i / 255);
    buf[i * 4 + 0] = r;
    buf[i * 4 + 1] = g;
    buf[i * 4 + 2] = b;
    buf[i * 4 + 3] = 255;
  }
  return buf;
}

// Lazy-cached so palette swaps at runtime don't re-interpolate each time.
const CACHE: Partial<Record<RenderColormapId, Uint8Array>> = {};

export function lutFor(id: RenderColormapId): Uint8Array {
  const cached = CACHE[id];
  if (cached) return cached;
  const anchors =
    id === 'pop'
      ? POP_ANCHORS
      : id === 'inferno'
      ? INFERNO_ANCHORS
      : id === 'viridis'
        ? VIRIDIS_ANCHORS
        : BLUE_ANCHORS;
  const lut = buildLut(anchors);
  CACHE[id] = lut;
  return lut;
}
