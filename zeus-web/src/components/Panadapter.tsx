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

import { useEffect, useRef, type CSSProperties } from 'react';
import { createPanRenderer, hexToRgbFloats } from '../gl/panadapter';
import { planForFrame } from '../gl/frame-plan';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { registerFrameConsumer, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { enhanceInto, useSignalEnhanceStore } from '../dsp/signal-estimator';
import * as viewCenter from '../state/view-center';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';
import { FilterCursorOverlay } from './FilterCursorOverlay';
import { FreqAxis } from './FreqAxis';
import { PassbandOverlay } from './PassbandOverlay';
import { ImdReadings } from './ImdReadings';
import { DbScale } from './DbScale';
import { SpotOverlay } from './SpotOverlay';
import { PeakMarkerOverlay } from './PeakMarkerOverlay';
import { NotchOverlay } from './NotchOverlay';

export function Panadapter() {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const popRenderIntensity = useSignalEnhanceStore((s) => s.popRenderIntensity);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const popActive = popEnabled && !moxOn && !tunOn;
  const popIntensityCss = Math.max(0, Math.min(1, popRenderIntensity / 100)).toFixed(2);

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
    // Anchor model (issue #597): the adopted trace is pinned to the center
    // frequency it was captured at; every draw renders it offset by
    // (anchorCenterHz − viewCenterHz) in FRACTIONAL pixels. Server frames
    // refresh the anchor content (outside the refill hold); the animated
    // view-center — not frame arrival — drives all horizontal motion. The
    // shift decision itself comes from the shared planner (gl/frame-plan.ts)
    // so the waterfall can never disagree with the trace by a frame.
    let anchorPan: Float32Array | null = null;
    let anchorCenterHz = 0;
    let anchorHzPerPixel = 0;
    // Signal Pop (issue: AI-enhance display). The adopted anchor is the raw
    // server trace UNLESS Pop is on, in which case it's the per-bin
    // floor-subtracted trace. We keep the last RAW trace around so a Pop toggle
    // (or a parameter change) can rebuild the anchor without waiting for the
    // next frame. Enhanced output is double-buffered so each adoption presents
    // a NEW Float32Array reference — the renderer's dataDirty check (issue #597)
    // keys on reference identity, so mutating one buffer in place would be
    // silently dropped during a glide.
    let lastRawPan: Float32Array | null = null;
    const enhScratch: Array<Float32Array | null> = [null, null];
    let enhSlot = 0;
    const buildAnchor = (raw: Float32Array): Float32Array => {
      const { popEnabled } = useSignalEnhanceStore.getState();
      const { moxOn, tunOn } = useTxStore.getState();
      // Pop is an RX weak-signal aid; the TX trace lives in a different dB
      // domain (speech against a calibrated scale), so leave it raw while keyed.
      if (!popEnabled || moxOn || tunOn) return raw;
      let buf = enhScratch[enhSlot];
      if (!buf || buf.length !== raw.length) {
        buf = new Float32Array(raw.length);
        enhScratch[enhSlot] = buf;
      }
      enhanceInto(raw, buf);
      enhSlot ^= 1;
      return buf;
    };
    // Visibility gating: don't burn rAF cycles when the tile is scrolled
    // off-screen, the tab is hidden, or the operator switched to a layout
    // where the panadapter isn't mounted-but-visible. Both signals are
    // ORed into a single `isActive` flag the requestRedraw guard checks.
    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;

    const redraw = () => {
      if (!anchorPan) return;
      const s = useDisplaySettingsStore.getState();
      // While keyed (MOX or TUN — server already feeds TX pixels via
      // DspPipelineService.Tick) use the TX-specific dB range so the
      // operator's RX noise-floor view is untouched. Thetis parity, see
      // TX_FIXED_DB_MIN/MAX in display-settings-store.
      const { moxOn, tunOn } = useTxStore.getState();
      const keyed = moxOn || tunOn;
      const pop = useSignalEnhanceStore.getState();
      // Signal Pop (RX only): the anchor now holds gated/compressed 0..1 display
      // values (enhanceInto), so the colormap maps [0,1] directly. Keyed/TX
      // keeps the absolute dB window.
      const popOn = pop.popEnabled && !keyed;
      const popIntensity = popOn ? Math.max(0, Math.min(1, pop.popRenderIntensity / 100)) : 0;
      const dbMin = popOn ? 0 : keyed ? s.txDbMin : s.dbMin;
      const dbMax = popOn ? 1 : keyed ? s.txDbMax : s.dbMax;
      const { r, g, b } = hexToRgbFloats(s.rxTraceColor);
      renderer.setTraceColor(r, g, b);
      renderer.setPopMode(popOn, popIntensity);
      // Fractional offset — the shaders take a float uOffsetPx, so the
      // glide is sub-pixel-smooth for free (issue #597).
      const offsetPx =
        anchorHzPerPixel > 0 && viewCenter.isInitialized()
          ? (anchorCenterHz - viewCenter.getViewCenterHz()) / anchorHzPerPixel
          : 0;
      renderer.draw(anchorPan, dbMin, dbMax, offsetPx);
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
      // The planner must see EVERY frame — including ones whose pan payload
      // is invalid — so its tracker can never drift against the waterfall's
      // view of the same stream (issue #597 dual-tracker divergence fix).
      const decision = planForFrame({
        seq: state.lastSeq,
        centerHz: state.centerHz,
        hzPerPixel: state.hzPerPixel,
        width: state.width,
      });
      const frameCenter = Number(state.centerHz);

      if (decision.kind === 'reset') {
        // Hard reset (first frame / width change / no-overlap jump): the old
        // anchor is meaningless. Snap the view — no glide — and adopt
        // immediately; the refill hold doesn't apply across a reset.
        viewCenter.snapTo(frameCenter, state.hzPerPixel);
        if (state.panValid && state.panDb) {
          lastRawPan = state.panDb;
          anchorPan = buildAnchor(state.panDb);
          anchorCenterHz = frameCenter;
          anchorHzPerPixel = state.hzPerPixel;
        }
      } else {
        // push/shift: feed the frame center back to the view-center. With no
        // recent operator gesture this recognises external tunes (CAT/TCI,
        // band buttons, typed entry, mode changes) and glides there — which
        // also arms the refill hold via the target-change stamp.
        viewCenter.reconcileFrame(frameCenter, state.hzPerPixel);
        // Adoption is unconditional (issue #597 Phase 2): the backend now
        // stamps CenterHz with the LO the pixels were actually computed at
        // (delay-compensated LO-history lookup), so mid-retune frames are
        // self-describing — the anchor model draws them where their data
        // belongs and the old refill-hold heuristic is unnecessary.
        if (state.panValid && state.panDb) {
          lastRawPan = state.panDb;
          anchorPan = buildAnchor(state.panDb);
          anchorCenterHz = frameCenter;
          anchorHzPerPixel = state.hzPerPixel;
        }
      }

      requestRedraw();
    });

    // Signal Pop toggle / tuning change: rebuild the anchor from the last raw
    // trace and repaint now, instead of waiting for the next server frame.
    const unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
      if (
        state.popEnabled !== prev.popEnabled ||
        state.popFloorDb !== prev.popFloorDb ||
        state.popSpanDb !== prev.popSpanDb ||
        state.popGamma !== prev.popGamma ||
        state.popRenderIntensity !== prev.popRenderIntensity ||
        state.coherenceHoldGate !== prev.coherenceHoldGate ||
        state.coherenceBoostDb !== prev.coherenceBoostDb ||
        state.ridgeBoost !== prev.ridgeBoost ||
        state.ridgeMaxBoostDb !== prev.ridgeMaxBoostDb ||
        state.visualAgcEnabled !== prev.visualAgcEnabled ||
        state.visualAgcStrength !== prev.visualAgcStrength ||
        state.impulseRejectEnabled !== prev.impulseRejectEnabled ||
        state.impulseRejectDb !== prev.impulseRejectDb
      ) {
        if (lastRawPan) anchorPan = buildAnchor(lastRawPan);
        requestRedraw();
      }
    });

    // View-center motion → redraw at display rate while gliding. The
    // subscription is silent when the tween loop is parked (zero idle cost).
    const unsubViewCenter = viewCenter.subscribe(requestRedraw);

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
        // buildAnchor gates Pop off while keyed, so rebuild from the last raw
        // trace on the MOX/TUN edge to avoid a one-frame enhanced-vs-TX-range
        // mismap before the first TX frame adopts.
        if (lastRawPan) anchorPan = buildAnchor(lastRawPan);
        requestRedraw();
      }
    });

    return () => {
      unsub();
      unsubViewCenter();
      unsubSettings();
      unsubTx();
      unsubEnhance();
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
      className={`spectrum-canvas${popActive ? ' pop-enhanced' : ''}`}
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: popActive ? 'var(--pop-surface-bg)' : 'var(--spec-bg)',
        ...(popActive
          ? ({ ['--pop-intensity' as string]: popIntensityCss } as CSSProperties)
          : undefined),
      }}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      <PassbandOverlay resizable containerRef={containerRef} />
      <FilterCursorOverlay containerRef={containerRef} />
      <SpotOverlay />
      <PeakMarkerOverlay />
      <NotchOverlay interactive resizable containerRef={containerRef} />
      <ImdReadings />
      <FreqAxis />
      <DbScale />
    </div>
  );
}
