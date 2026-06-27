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

import { useEffect, useRef, type CSSProperties } from 'react';
import {
  cancelPendingPanContextLoss,
  createPanRenderer,
  hexToRgbFloats,
  schedulePanContextLoss,
} from '../gl/panadapter';
import { planForFrame } from '../gl/frame-plan';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { registerFrameConsumer, selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore, shouldTxAutoRange } from '../state/display-settings-store';
import { useConnectionStore } from '../state/connection-store';
import { enhanceInto, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { normalizeStitchedBins, stitchFloorShiftDb } from '../dsp/stitch-normalizer';
import { getReceiverVfoHz, receiverLabel, rxIndexOf, type ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import * as viewZoom from '../state/view-zoom';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture, type PanTuneGestureOptions } from '../util/use-pan-tune-gesture';
import { BandOverlay } from './BandOverlay';
import { FilterCursorOverlay } from './FilterCursorOverlay';
import { FreqAxis } from './FreqAxis';
import { PassbandOverlay } from './PassbandOverlay';
import { ImdReadings } from './ImdReadings';
import { DbScale } from './DbScale';
import { SpotOverlay } from './SpotOverlay';
import { ChatRosterOverlay } from './ChatRosterOverlay';
import { PeakMarkerOverlay } from './PeakMarkerOverlay';
import { NotchOverlay } from './NotchOverlay';
import { spectrumReceiverFilterColor } from './spectrumReceiverColor';

type PanadapterProps = {
  receiver?: ReceiverKey;
  touchMode?: PanTuneGestureOptions['touchMode'];
  tuneReceiver?: PanTuneGestureOptions['tuneReceiver'];
  stitched?: boolean;
  foreground?: boolean;
  multiRx?: boolean;
};

export function Panadapter({
  receiver = 'A',
  touchMode = 'normal',
  tuneReceiver,
  stitched = false,
  foreground = true,
  multiRx = false,
}: PanadapterProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rxIndex = rxIndexOf(receiver);
  const vfoHz = useConnectionStore((s) => getReceiverVfoHz(s, receiver));
  // Operator-facing label for the overlay. Hardware DDCs fall back to "RX{n}";
  // the Kiwi slice receiver carries a name ("Kiwi") on its receivers[] entry.
  const rxLabel = useConnectionStore((s) =>
    receiverLabel({ index: rxIndex, name: s.receivers.find((r) => r.index === rxIndex)?.name }),
  );
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const popRenderIntensity = useSignalEnhanceStore((s) => s.popRenderIntensity);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const popActive = popEnabled && !moxOn && !tunOn;
  const popIntensityCss = Math.max(0, Math.min(1, popRenderIntensity / 100)).toFixed(2);
  const receiverFilterColor = spectrumReceiverFilterColor(receiver);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    // A previous unmount may have scheduled a deferred context loss for this
    // canvas; cancel it now that we're reusing the canvas so we don't tear down
    // the context we're about to (re)create (mirrors Waterfall.tsx, #629).
    cancelPendingPanContextLoss(canvas);

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
    // Double-buffered like enhScratch: normalizeStitchedBins reuses the scratch
    // it's handed and returns the SAME reference, but the pan renderer's
    // dataDirty check keys on reference identity — a single reused buffer would
    // be silently dropped and freeze the trace (RX2 stitched half). Alternating
    // two buffers presents a fresh reference each adoption.
    const stitchScratch: Array<Float32Array | null> = [null, null];
    let stitchSlot = 0;
    let enhSlot = 0;
    const buildAnchor = (raw: Float32Array): Float32Array => {
      const { popEnabled } = useSignalEnhanceStore.getState();
      const { moxOn, tunOn } = useTxStore.getState();
      let source = raw;
      if (stitched && !moxOn && !tunOn) {
        source = normalizeStitchedBins(
          raw,
          stitchScratch[stitchSlot] ?? null,
          stitchFloorShiftDb(receiver, 'pan'),
        );
        // Only retain/alternate when a shift was actually applied (source is the
        // scratch). When no shift is applied normalizeStitchedBins returns raw
        // — caching raw as scratch would let the next frame overwrite the live
        // store buffer in place.
        if (source !== raw) {
          stitchScratch[stitchSlot] = source;
          stitchSlot ^= 1;
        }
      }
      // Pop is an RX weak-signal aid; the TX trace lives in a different dB
      // domain (speech against a calibrated scale), so leave it raw while keyed.
      if (!popEnabled || moxOn || tunOn) return source;
      let buf = enhScratch[enhSlot];
      if (!buf || buf.length !== source.length) {
        buf = new Float32Array(source.length);
        enhScratch[enhSlot] = buf;
      }
      enhanceInto(source, buf);
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
    // Per-receiver animated view-center: RX1 and RX2/VFO B each glide their own
    // pan motion, so both stitched halves move smoothly and identically.
    const vc = viewCenter.viewCenterFor(receiver);
    const visualCenterHz = () =>
      vc.isInitialized()
        ? vc.getViewCenterHz()
        : Number(selectDisplaySlice(useDisplayStore.getState(), receiver).centerHz);

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
        anchorHzPerPixel > 0
          ? (anchorCenterHz - visualCenterHz()) / anchorHzPerPixel
          : 0;
      // Draw-time zoom (view-zoom.ts): scale the trace about the view centre
      // when the animated display span lags the span this anchor was captured
      // at, so the trace scales in lock-step with the waterfall during a zoom.
      // The Kiwi slice receiver self-scales to its OWN frame Hz/pixel (it has an
      // independent span the RX1-driven global tween knows nothing about), which
      // makes scaleX resolve to 1 (full width); every hardware DDC follows the
      // shared zoom tween exactly as before. See displayedHzPerPixelFor.
      const displayedHzPerPixel = viewZoom.displayedHzPerPixelFor(rxIndex, anchorHzPerPixel);
      const scaleX =
        displayedHzPerPixel > 0 && anchorHzPerPixel > 0
          ? anchorHzPerPixel / displayedHzPerPixel
          : 1;
      renderer.draw(anchorPan, dbMin, dbMax, offsetPx, scaleX);
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
      const slice = selectDisplaySlice(state, receiver);
      if (slice.lastSeq === 0) return;
      if (slice.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = slice.lastSeq;
      // The planner must see EVERY frame — including ones whose pan payload
      // is invalid — so its tracker can never drift against the waterfall's
      // view of the same stream (issue #597 dual-tracker divergence fix).
      const decision = planForFrame({
        seq: slice.lastSeq,
        centerHz: slice.centerHz,
        hzPerPixel: slice.hzPerPixel,
        width: slice.width,
        planKey: String(receiver),
      });
      const frameCenter = Number(slice.centerHz);

      // Drive the shared zoom tween (view-zoom.ts) from RX1 only — see the
      // matching block in Waterfall.tsx. Idempotent with the waterfall's call;
      // having both A surfaces drive it keeps zoom animating in layouts where
      // only one of them is mounted.
      if (rxIndex === 0 && slice.hzPerPixel > 0) {
        if (decision.kind === 'reset') viewZoom.snapTo(slice.hzPerPixel);
        else viewZoom.setTarget(slice.hzPerPixel);
      }

      if (decision.kind === 'reset') {
        // Hard reset (first frame / width change / no-overlap jump): the old
        // anchor is meaningless. Snap the view — no glide — and adopt
        // immediately; the refill hold doesn't apply across a reset.
        vc.snapTo(frameCenter, slice.hzPerPixel);
        if (slice.panValid && slice.panDb) {
          lastRawPan = slice.panDb;
          anchorPan = buildAnchor(slice.panDb);
          anchorCenterHz = frameCenter;
          anchorHzPerPixel = slice.hzPerPixel;
        }
      } else {
        // push/shift: feed the frame center back to the view-center. With no
        // recent operator gesture this recognises external tunes (CAT/TCI,
        // band buttons, typed entry, mode changes) and glides there — which
        // also arms the refill hold via the target-change stamp.
        vc.reconcileFrame(frameCenter, slice.hzPerPixel);
        // Adoption is unconditional (issue #597 Phase 2): the backend now
        // stamps CenterHz with the LO the pixels were actually computed at
        // (delay-compensated LO-history lookup), so mid-retune frames are
        // self-describing — the anchor model draws them where their data
        // belongs and the old refill-hold heuristic is unnecessary.
        if (slice.panValid && slice.panDb) {
          lastRawPan = slice.panDb;
          anchorPan = buildAnchor(slice.panDb);
          anchorCenterHz = frameCenter;
          anchorHzPerPixel = slice.hzPerPixel;
        }
      }

      // While transmitting voice (MOX/PTT), fit the TX display windows to the
      // live signal. TUNE and two-tone are excluded — see shouldTxAutoRange();
      // their narrow carrier would collapse the fit onto the noise floor. The
      // panadapter is the always-present TX surface, so it drives the fit
      // regardless of which waterfall renderer is active. Receiver A only —
      // that's the slice the server feeds TX pixels into; RX2 (receiver B)
      // keeps its own RX window during TX.
      if (rxIndex === 0 && slice.panValid && slice.panDb) {
        const tx = useTxStore.getState();
        const ds = useDisplaySettingsStore.getState();
        if (shouldTxAutoRange(tx, ds.txAutoRange)) ds.updateTxAutoRange(slice.panDb);
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
    const unsubViewCenter = vc.subscribe(requestRedraw);
    // Zoom motion → redraw while the display span eases (draw-time scale).
    const unsubViewZoom = viewZoom.subscribe(requestRedraw);
    const unsubConn = useConnectionStore.subscribe((state, prev) => {
      // Secondary receivers (RX2 / RX3+) center on their VFO, so redraw when it
      // moves. Every secondary lives in the receivers[] array (RX2 = index 1),
      // whose reference changes on any per-receiver update.
      if (rxIndex === 0) return;
      if (state.receivers !== prev.receivers) requestRedraw();
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
      if (
        state.moxOn !== prev.moxOn ||
        state.tunOn !== prev.tunOn ||
        state.twoToneOn !== prev.twoToneOn
      ) {
        // When auto-range isn't engaging for this TX type (TUNE / two-tone, or
        // master off), snap the TX window back to the fixed/saved range so a
        // carrier left narrow by a prior voice-MOX fit renders clean. Voice MOX
        // lets the per-frame fit drive it instead.
        const ds = useDisplaySettingsStore.getState();
        if (!shouldTxAutoRange(state, ds.txAutoRange)) ds.restoreSavedTxWindows();
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
      unsubViewZoom();
      unsubConn();
      unsubSettings();
      unsubTx();
      unsubEnhance();
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      cancelDrawBusFrame(redraw);
      renderer.dispose();
      // Free the ANGLE context slot on real unmounts, deferred so a StrictMode
      // (or quick drag-induced) remount can cancel it and reuse the canvas
      // rather than losing a live context (#629). dispose() frees GL objects;
      // this releases the context itself so contexts don't accumulate across
      // repeated workspace rearranges.
      schedulePanContextLoss(canvas, gl);
      releaseFrameConsumer();
    };
  }, [receiver, stitched]);

  usePanTuneGesture(canvasRef, receiver, { touchMode, tuneReceiver });

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
        opacity: 1,
        ['--vfo-filter-color' as string]: receiverFilterColor,
        ...(popActive
          ? ({ ['--pop-intensity' as string]: popIntensityCss } as CSSProperties)
          : undefined),
      } as CSSProperties}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      {rxIndex === 0 && !multiRx && <BandOverlay receiver={receiver} />}
      <div
        className="pointer-events-none absolute z-[25] rounded-sm px-2 py-0.5 font-mono text-[10px]"
        style={{
          top: 24,
          left: 8,
          background: 'rgba(8, 10, 14, 0.78)',
          color: receiverFilterColor,
          border: '1px solid rgba(255,255,255,0.16)',
        }}
      >
        {rxLabel} · {(vfoHz / 1e6).toFixed(6)}
        {stitched && foreground ? ' · FOCUS' : ''}
      </div>
      {/* Passband + hover crosshair render on BOTH halves (RX2), each tracking
          its own receiver's geometry, so a click lands wherever the operator
          points — not only on the focused half. Mirrors the WebGPU heightfield. */}
      <PassbandOverlay resizable containerRef={containerRef} receiver={receiver} />
      <FilterCursorOverlay containerRef={containerRef} receiver={receiver} />
      {rxIndex === 0 && (!stitched || foreground) && (
        <>
          <SpotOverlay />
          <ChatRosterOverlay />
          <PeakMarkerOverlay />
          <NotchOverlay interactive resizable containerRef={containerRef} />
          <ImdReadings />
        </>
      )}
      <FreqAxis receiver={receiver} stitched={stitched} />
      {/* One global dB scale for the whole stack — only RX1 (leftmost) renders
          it; every other pane (RX2 stitched half, RX3+ standalone) shares it. */}
      {rxIndex === 0 && <DbScale />}
    </div>
  );
}
