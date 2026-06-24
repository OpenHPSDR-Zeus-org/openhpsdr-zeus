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

import { useEffect, useRef } from 'react';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { getReceiverVfoHz, type ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import { useRulerPanGesture } from '../util/use-ruler-pan-gesture';

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

// Overlay rendered inside Panadapter's container. Tick LABELS are laid out
// by React at the 30 Hz frame rate (percentage of span around the frame's
// centerHz, exactly as before); smooth horizontal MOTION between frames is
// applied imperatively — a draw-bus callback translates the tick strip and
// repositions the dial marker against the animated view-center
// (state/view-center.ts), so motion runs at display rate with ZERO React
// commits per display frame (issue #597; ticks are rigid under a pure pan).
// The dial-marker line tracks VfoHz, which equals centerHz outside CW and sits
// ±cw_pitch from centre in CWU/CWL.
type FreqAxisProps = {
  receiver?: ReceiverKey;
  stitched?: boolean;
};

export function FreqAxis({ receiver = 'A', stitched = false }: FreqAxisProps = {}) {
  const centerHz = useDisplayStore((s) => selectDisplaySlice(s, receiver).centerHz);
  const hzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  // Header width — present even on frames whose pan payload is invalid, so
  // the axis doesn't unmount during a brief invalid-frame run.
  const width = useDisplayStore((s) => selectDisplaySlice(s, receiver).width);
  // NOTE deliberately NO vfoHz selector: during a tuning gesture vfoHz
  // updates at input rate and a selector here would re-render this
  // component at display rate. The draw-bus callback reads it directly.

  const tickStripRef = useRef<HTMLDivElement | null>(null);
  const markerRef = useRef<HTMLDivElement | null>(null);
  const rulerRef = useRef<HTMLDivElement | null>(null);

  useRulerPanGesture(rulerRef, receiver, !!width && hzPerPixel > 0);

  useEffect(() => {
    const vc = viewCenter.viewCenterFor(receiver);
    const update = () => {
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (!s.width || s.hzPerPixel <= 0) return;
      const spanHz = s.width * s.hzPerPixel;
      const conn = useConnectionStore.getState();
      const layoutCenter = Number(s.centerHz);
      const view = vc.isInitialized() ? vc.getViewCenterHz() : layoutCenter;
      // Ticks were laid out around layoutCenter; sliding the strip by the
      // layout→view fraction of its own width keeps every label at its true
      // frequency. translateX(%) is relative to the element's own width,
      // which equals the container width (inset-x-0).
      const strip = tickStripRef.current;
      if (strip) {
        const fracPct = ((layoutCenter - view) / spanHz) * 100;
        strip.style.transform = `translateX(${fracPct}%)`;
      }
      const marker = markerRef.current;
      if (marker) {
        // The marker's distance from the zero line is the dial's settled
        // offset from the display center: 0 outside CW, ±cw_pitch in
        // CWU/CWL. Computing it as (vfo − commanded target) keeps the
        // marker PINNED to the zero line during a glide (vfo and target
        // move in lockstep at input time) instead of leading off it and
        // easing back (operator feedback, 2026-06-12).
        const vfoHz = getReceiverVfoHz(conn, receiver);
        const dialOffsetHz = vc.isInitialized()
          ? vfoHz - vc.getTargetCenterHz()
          : vfoHz - layoutCenter;
        marker.style.left = `${((spanHz / 2 + dialOffsetHz) / spanHz) * 100}%`;
      }
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = vc.subscribe(schedule);
    const unsubVfo = useConnectionStore.subscribe((s, prev) => {
      // RX1 is the flat primary VFO; every secondary (RX2 = index 1, RX3+) lives
      // in the receivers[] array.
      if (s.vfoHz !== prev.vfoHz || s.receivers !== prev.receivers) schedule();
    });
    const unsubFrame = useDisplayStore.subscribe((s, prev) => {
      if (selectDisplaySlice(s, receiver).lastSeq !== selectDisplaySlice(prev, receiver).lastSeq) schedule();
    });
    schedule();
    return () => {
      unsubVc();
      unsubVfo();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, [receiver]);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const stride = pickStrideHz(spanHz, 6);
  const conn = useConnectionStore.getState();
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;
  const endHz = center + spanHz / 2;
  // Initial (pre-draw-bus) marker position; the callback refines it against
  // the animated view-center on the next frame.
  const selectedVfoHz = getReceiverVfoHz(conn, receiver);
  const dialPct = ((selectedVfoHz - startHz) / spanHz) * 100;

  // Lay ticks out one full stride beyond each edge so a glide can't expose
  // a label-less gap before the next 30 Hz relayout catches up.
  const firstIdx = Math.ceil((startHz - stride) / stride);
  const lastIdx = Math.floor((endHz + stride) / stride);
  const ticks: { hz: number; pct: number }[] = [];
  for (let i = firstIdx; i <= lastIdx; i++) {
    const hz = i * stride;
    ticks.push({ hz, pct: ((hz - startHz) / spanHz) * 100 });
  }

  return (
    <>
      {/* Outer strip: fixed background + overflow clip. Inner strip: the
          tick content, slid by the draw-bus callback — so the background
          never moves and overscanned ticks scroll in from the edges. */}
      <div
        ref={rulerRef}
        className="pointer-events-auto absolute inset-x-0 top-0 z-10 h-5 overflow-hidden bg-neutral-950/70"
        style={{ touchAction: 'none', userSelect: 'none' }}
        title="Drag to pan the frequency view"
      >
        <div ref={tickStripRef} className="pointer-events-none absolute inset-0">
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
      </div>
      {/*
        Dial-position marker — sits at VfoHz, which equals centerHz outside
        CW and is offset by ±cw_pitch from centre in CWU/CWL. The marker uses
        the same receiver colour as the passband overlay.
       */}
      {!stitched && (
        <div
          ref={markerRef}
          className="pointer-events-none absolute inset-y-0 z-[15] -translate-x-1/2"
          style={{ left: `${dialPct}%`, width: 2, background: 'var(--vfo-filter-color, var(--accent))' }}
        />
      )}
    </>
  );
}
