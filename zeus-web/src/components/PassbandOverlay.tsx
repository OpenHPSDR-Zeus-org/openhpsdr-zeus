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

// Translucent rectangle drawn inside the panadapter container to show the
// active receive filter passband, mapped from [filterLowHz, filterHighHz]
// relative to the tuned dial (VfoHz). Asymmetric by design: USB lives to the
// right of carrier, LSB to the left, CW narrow around zero, AM symmetric.
// Positioned by percentage of the total span so it tracks resize and tune
// without measuring DOM width.
//
// The panadapter centres on the hardware NCO (radioLoHz) while vfoHz roams
// independently. Anchoring the passband to vfoHz — not centerHz — keeps the
// filter overlay glued to the operator's tuned signal so clicking the
// spectrum visibly slides the passband around the (stationary) waterfall.
export function PassbandOverlay() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  // Pure-pan viewport offset shifts the rendered window relative to the
  // hardware NCO; the passband must shift with it so it stays glued to the
  // dial frequency on-screen.
  const viewportOffsetHz = useDisplayStore((s) => s.viewportOffsetHz);
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);
  const vfoHz = useConnectionStore((s) => s.vfoHz);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz) + viewportOffsetHz;
  const startHz = center - spanHz / 2;

  const passLowHz = vfoHz + filterLowHz;
  const passHighHz = vfoHz + filterHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;
  const widthPct = rightPct - leftPct;

  if (widthPct <= 0 || leftPct > 100 || rightPct < 0) return null;

  return (
    <div
      aria-hidden
      className="pointer-events-none absolute inset-y-0 z-[5]"
      style={{
        left: `${leftPct}%`,
        width: `${widthPct}%`,
        background: 'rgba(255, 160, 40, 0.18)',
        borderLeft: '1px solid rgba(255, 160, 40, 0.6)',
        borderRight: '1px solid rgba(255, 160, 40, 0.6)',
      }}
    />
  );
}
