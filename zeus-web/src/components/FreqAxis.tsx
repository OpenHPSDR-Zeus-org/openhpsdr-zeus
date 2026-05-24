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

import { useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';

function pickStrideHz(spanHz: number, targetTicks: number): number {
  if (spanHz <= 0) return 1;
  const rough = spanHz / targetTicks;
  const pow = Math.pow(10, Math.floor(Math.log10(rough)));
  const n = rough / pow;
  let nice: number;
  if (n < 1.5) nice = 1;
  else if (n < 3.5) nice = 2;
  else if (n < 7.5) nice = 5;
  else nice = 10;
  return nice * pow;
}

function formatMHz(hz: number, strideHz: number): string {
  const mhz = hz / 1e6;
  if (strideHz >= 100_000) return mhz.toFixed(2);
  if (strideHz >= 10_000) return mhz.toFixed(3);
  if (strideHz >= 1_000) return mhz.toFixed(4);
  if (strideHz >= 100) return mhz.toFixed(5);
  return mhz.toFixed(6);
}

// Overlay rendered inside Panadapter's container. Positions ticks by
// percentage of the total span so it stays aligned without measuring DOM
// width: spanHz = panDb.length * hzPerPixel; centerHz is the radio's
// physical LO and lands at 50%. The amber dial-marker line tracks
// VfoHz, which equals centerHz outside CW and sits ±cw_pitch from
// centre in CWU/CWL — in non-CW the marker stays at 50% (zero offset).
export function FreqAxis() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const vfoHz = useConnectionStore((s) => s.vfoHz);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const stride = pickStrideHz(spanHz, 6);
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;
  const endHz = center + spanHz / 2;
  const dialPct = ((vfoHz - startHz) / spanHz) * 100;

  const firstIdx = Math.ceil(startHz / stride);
  const lastIdx = Math.floor(endHz / stride);
  const ticks: { hz: number; pct: number }[] = [];
  for (let i = firstIdx; i <= lastIdx; i++) {
    const hz = i * stride;
    ticks.push({ hz, pct: ((hz - startHz) / spanHz) * 100 });
  }

  return (
    <>
      <div className="pointer-events-none absolute inset-x-0 top-0 z-10 h-5 bg-neutral-950/70">
        {ticks.map((t) => (
          <div
            key={t.hz}
            className="absolute top-0 -translate-x-1/2 font-mono text-[10px] leading-none text-neutral-300"
            style={{ left: `${t.pct}%` }}
          >
            <div className="mx-auto h-1.5 w-px bg-neutral-400" />
            <div className="mt-0.5 px-1 whitespace-nowrap">
              {formatMHz(t.hz, stride)}
            </div>
          </div>
        ))}
      </div>
      {/*
        Dial-position marker — sits at VfoHz, which equals centerHz outside
        CW and is offset by ±cw_pitch from centre in CWU/CWL. In CW the
        marker lives inside the (amber) passband overlay, so it uses the
        accent blue + a 2px width to read clearly against the amber fill.
       */}
      <div
        className="pointer-events-none absolute inset-y-0 z-[15] -translate-x-1/2"
        style={{ left: `${dialPct}%`, width: 2, background: 'var(--accent)' }}
      />
    </>
  );
}
