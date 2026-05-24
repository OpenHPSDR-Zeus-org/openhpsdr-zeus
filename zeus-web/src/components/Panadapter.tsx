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
import { createPanRenderer, hexToRgbFloats } from '../gl/panadapter';
import { planWaterfallUpdate } from '../gl/wf-shift';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { registerFrameConsumer, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';
import { FreqAxis } from './FreqAxis';
import { PassbandOverlay } from './PassbandOverlay';
import { DbScale } from './DbScale';

export function Panadapter() {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const gl = canvas.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: true });
    if (!gl) {
      console.error('WebGL2 not available');
      return;
    }

    // Tell the realtime client that decoded spectrum frames are needed —
    // ws-client.ts skips decodeDisplayFrame entirely when no consumer is
    // registered (all spectrum surfaces closed).
    const releaseFrameConsumer = registerFrameConsumer();

    const renderer = createPanRenderer(gl);
    // Mirror the waterfall's shift state so pan and wf agree on what a VFO
    // retune does to the spectrum. On a 'shift' tick the waterfall suppresses
    // its new row and shifts the old history (doc 08 §5); the panadapter
    // shows the prior trace with the same x-offset so the two views line up.
    // On 'push'/'reset' the offset is 0 and the freshest trace is drawn.
    let lastPan: Float32Array | null = null;
    let lastCenterHz: bigint | null = null;
    let lastHzPerPixel = 0;
    let lastWidth = 0;
    let drawPan: Float32Array | null = null;
    let drawOffsetPx = 0;
    // Visibility gating: don't burn rAF cycles when the tile is scrolled
    // off-screen, the tab is hidden, or the operator switched to a layout
    // where the panadapter isn't mounted-but-visible. Both signals are
    // ORed into a single `isActive` flag the requestRedraw guard checks.
    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;

    const redraw = () => {
      if (!drawPan) return;
      const s = useDisplaySettingsStore.getState();
      // While keyed (MOX or TUN — server already feeds TX pixels via
      // DspPipelineService.Tick) use the TX-specific dB range so the
      // operator's RX noise-floor view is untouched. Thetis parity, see
      // TX_FIXED_DB_MIN/MAX in display-settings-store.
      const { moxOn, tunOn } = useTxStore.getState();
      const keyed = moxOn || tunOn;
      const dbMin = keyed ? s.txDbMin : s.dbMin;
      const dbMax = keyed ? s.txDbMax : s.dbMax;
      const { r, g, b } = hexToRgbFloats(s.rxTraceColor);
      renderer.setTraceColor(r, g, b);
      renderer.draw(drawPan, dbMin, dbMax, drawOffsetPx);
    };
    const requestRedraw = () => {
      if (!isActive()) return;
      // Shared draw bus: panadapter + waterfall coalesce onto a single rAF
      // per frame. The bus dedupes repeated requests for the same callback,
      // matching the prior `if (rafHandle === 0)` gate.
      requestDrawBusFrame(redraw);
    };

    const resize = () => {
      const { width, height } = container.getBoundingClientRect();
      // Clamp the WebGL backing store at DPR=1. On a Retina display the
      // native devicePixelRatio is 2 (or higher on 5K), which means the
      // panadapter would render at 4× the pixels and feed 4× the texture
      // data through every composite. The trace is a single-pixel-wide line
      // over a smooth dB gradient — sub-pixel antialiasing is not visible
      // and not worth the GPU cost. Browser CSS scaling fills the difference.
      const dpr = Math.min(1, window.devicePixelRatio || 1);
      const w = Math.max(1, Math.round(width * dpr));
      const h = Math.max(1, Math.round(height * dpr));
      canvas.width = w;
      canvas.height = h;
      renderer.resize(w, h);
      requestRedraw();
    };

    const ro = new ResizeObserver(resize);
    ro.observe(container);
    resize();

    // Pause WebGL when the panadapter is not actually visible. Two signals:
    // IntersectionObserver covers "tile scrolled out of view / display:none
    // ancestor", and document.visibilitychange covers "tab in background".
    // When we transition back to active, kick a redraw so the operator
    // sees the latest pushed frame immediately rather than waiting for the
    // next store update.
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) {
          inViewport = e.isIntersecting;
        }
        if (isActive()) requestRedraw();
      },
      { threshold: 0 },
    );
    io.observe(container);
    const onVisibilityChange = () => {
      pageVisible = !document.hidden;
      if (isActive()) requestRedraw();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    let lastSeqDrawn = -1;
    const unsub = useDisplayStore.subscribe((state) => {
      if (state.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = state.lastSeq;
      if (!state.panValid || !state.panDb) return;

      const decision = planWaterfallUpdate({
        lastCenterHz,
        lastHzPerPixel,
        lastWidth,
        nextCenterHz: state.centerHz,
        nextHzPerPixel: state.hzPerPixel,
        nextWidth: state.panDb.length,
      });

      switch (decision.kind) {
        case 'reset':
          drawPan = state.panDb;
          drawOffsetPx = 0;
          lastPan = state.panDb;
          lastCenterHz = state.centerHz;
          lastHzPerPixel = state.hzPerPixel;
          lastWidth = state.panDb.length;
          break;
        case 'push':
          drawPan = state.panDb;
          drawOffsetPx = 0;
          lastPan = state.panDb;
          // lastCenterHz unchanged so sub-pixel retunes accumulate.
          break;
        case 'shift':
          // Show the last pushed frame with the accumulated integer-pixel
          // offset the waterfall has applied to its history — the post-shift
          // top row and this trace land the same carriers in the same
          // columns. Offset accumulates across consecutive shift ticks and
          // resets on the next push (which updates lastPan to fresh data).
          drawPan = lastPan ?? state.panDb;
          drawOffsetPx += decision.shiftPx;
          lastCenterHz = decision.residualCenterHz;
          break;
      }

      requestRedraw();
    });

    // Repaint on dB-range / trace-color updates so auto-range and the Display
    // settings panel apply without waiting for the next server frame. The
    // prev-state diff is the load-bearing part: a no-selector subscribe used
    // to fire on every store mutation, which during ordinary RX traffic
    // pulled the panadapter rAF floor above the spectrum-tick rate.
    const unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
      if (
        state.dbMin !== prev.dbMin ||
        state.dbMax !== prev.dbMax ||
        state.txDbMin !== prev.txDbMin ||
        state.txDbMax !== prev.txDbMax ||
        state.rxTraceColor !== prev.rxTraceColor
      ) {
        requestRedraw();
      }
    });

    // Repaint when MOX / TUN flips so the RX-vs-TX dB range swap is
    // reflected immediately, even if no fresh pan frame arrived yet.
    // App.tsx:211 uses the same prev-state diff pattern — without it the
    // unconditional subscriber fires on every tx-store update (mic dBFS at
    // 50 Hz from the worklet, RxDbm at 5 Hz, PaTempC at 2 Hz, etc.), which
    // raises the floor on the redraw rate above the spectrum-tick rate.
    const unsubTx = useTxStore.subscribe((state, prev) => {
      if (state.moxOn !== prev.moxOn || state.tunOn !== prev.tunOn) {
        requestRedraw();
      }
    });

    return () => {
      unsub();
      unsubSettings();
      unsubTx();
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      cancelDrawBusFrame(redraw);
      renderer.dispose();
      releaseFrameConsumer();
    };
  }, []);

  usePanTuneGesture(canvasRef);

  return (
    <div
      ref={containerRef}
      className="spectrum-canvas"
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: 'var(--spec-bg)',
      }}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      <PassbandOverlay />
      <FreqAxis />
      <DbScale />
    </div>
  );
}
