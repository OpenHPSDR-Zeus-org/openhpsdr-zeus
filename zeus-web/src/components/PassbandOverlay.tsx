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

import { useEffect, useRef } from 'react';
import { useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import * as viewCenter from '../state/view-center';

// Translucent rectangle drawn inside the panadapter container to show the
// active receive filter passband, mapped from [filterLowHz, filterHighHz]
// relative to the VFO centre. Asymmetric by design: USB lives to the right
// of carrier, LSB to the left, CW narrow around zero, AM symmetric.
// Positioned by percentage of the total span so it tracks resize and tune
// without measuring DOM width.
export function PassbandOverlay() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  // Header width — survives frames whose pan payload is invalid.
  const width = useDisplayStore((s) => s.width);
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);

  const rectRef = useRef<HTMLDivElement | null>(null);

  // Smooth motion (issue #597): the rect is positioned against the animated
  // view-center by a draw-bus callback — same clock as the trace, waterfall,
  // dial marker, and tick strip, zero React commits at display rate. The
  // passband rides the radio's center (filter edges are center-relative),
  // so during a glide it eases with the spectrum instead of teleporting at
  // 30 Hz frame arrival.
  useEffect(() => {
    const update = () => {
      const rect = rectRef.current;
      if (!rect) return;
      const s = useDisplayStore.getState();
      if (!s.width || s.hzPerPixel <= 0) return;
      const spanHz = s.width * s.hzPerPixel;
      const view = viewCenter.isInitialized()
        ? viewCenter.getViewCenterHz()
        : Number(s.centerHz);
      // The passband hangs off the VIEW center — which is, by definition,
      // always rendered at the screen center (the orange zero line). So
      // during a tuning glide the filter stays PINNED to the line while the
      // spectrum slides underneath it; anchoring to the commanded target
      // instead made it lead off the line and ease back (operator feedback,
      // 2026-06-12).
      const conn = useConnectionStore.getState();
      // Hang the passband off the dial, expressed as the dial's settled offset
      // from the display center — the same (vfo − targetCenter) the FreqAxis
      // marker uses. Outside CTUN the dial sits on the view center so this is
      // ~0 and the filter stays pinned to the zero line during a glide; under
      // CTUN the dial roams off-centre and the passband tracks it.
      const dialOffsetHz = viewCenter.isInitialized()
        ? conn.vfoHz - viewCenter.getTargetCenterHz()
        : 0;
      const passCenter = view + dialOffsetHz;
      const startHz = view - spanHz / 2;
      const leftPct = ((passCenter + conn.filterLowHz - startHz) / spanHz) * 100;
      const rightPct = ((passCenter + conn.filterHighHz - startHz) / spanHz) * 100;
      const widthPct = rightPct - leftPct;
      const visible = widthPct > 0 && leftPct <= 100 && rightPct >= 0;
      rect.style.display = visible ? '' : 'none';
      if (visible) {
        rect.style.left = `${leftPct}%`;
        rect.style.width = `${widthPct}%`;
      }
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = viewCenter.subscribe(schedule);
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (
        s.filterLowHz !== prev.filterLowHz ||
        s.filterHighHz !== prev.filterHighHz ||
        s.vfoHz !== prev.vfoHz
      ) {
        schedule();
      }
    });
    const unsubFrame = useDisplayStore.subscribe((s, prev) => {
      if (s.lastSeq !== prev.lastSeq) schedule();
    });
    schedule();
    return () => {
      unsubVc();
      unsubConn();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, []);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;

  // Initial (pre-draw-bus) geometry; the callback refines it next frame.
  const passLowHz = center + filterLowHz;
  const passHighHz = center + filterHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;
  const widthPct = rightPct - leftPct;

  if (widthPct <= 0) return null;

  return (
    <div
      ref={rectRef}
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
