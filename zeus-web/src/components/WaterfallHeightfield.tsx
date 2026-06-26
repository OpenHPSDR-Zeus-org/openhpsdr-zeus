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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// WebGPU shaded-relief waterfall surface (memory: waterfall-nextgen-goal). The
// DEFAULT waterfall; WaterfallSurface mounts it whenever WebGPU is available and
// falls back to the WebGL Waterfall otherwise (or on device loss, via
// onUnavailable). A read-only consumer of the live display frames.
//
// Renderer init is async (requestAdapter/requestDevice), so this carries its own
// loading/unsupported states. Pan/zoom glide and the dB/colormap/scroll sliders
// come from the same shared stores as the WebGL surface; the zoom/pan tweens are
// driven by the still-mounted Panadapter — here we only consume them. Draws are
// coalesced onto the shared draw bus (one paint per frame).

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { probeWebGpu, resetWebGpuProbe } from '../gl/webgpu/caps';
import { createHeightfieldRenderer, type HeightfieldRenderer } from '../gl/webgpu/heightfield';
import { registerFrameConsumer, selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { enhanceInto, registerEstimatorConsumer, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { normalizeStitchedBins, stitchFloorShiftDb } from '../dsp/stitch-normalizer';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { getReceiverVfoHz, KIWI_RECEIVER_INDEX, rxIndexOf, type ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import * as viewZoom from '../state/view-zoom';
import { usePanTuneGesture, type PanTuneGestureOptions } from '../util/use-pan-tune-gesture';
import { FilterCursorOverlay } from './FilterCursorOverlay';
import { NotchOverlay } from './NotchOverlay';
import { PassbandOverlay } from './PassbandOverlay';
import { WfDbScale } from './WfDbScale';
import { spectrumReceiverFilterColor } from './spectrumReceiverColor';

type Props = {
  receiver?: ReceiverKey;
  /** Dev overlay: a live frame-time / fps readout. Off by default (production). */
  showStats?: boolean;
  /** Stitched dual-RX (RX2) layout — gates which overlays render and applies the
   *  per-half floor normalisation, matching the WebGL Waterfall. */
  stitched?: boolean;
  /** In stitched mode, whether this half currently has focus (drives passband /
   *  filter overlay placement). Ignored when not stitched. */
  foreground?: boolean;
  touchMode?: PanTuneGestureOptions['touchMode'];
  tuneReceiver?: PanTuneGestureOptions['tuneReceiver'];
  /** Render the in-tile dB scale on RX1. Off when the parent draws one scale
   *  spanning the whole (possibly multi-row) waterfall grid instead. */
  dbScale?: boolean;
  /** Called once if WebGPU is unavailable, the renderer fails to init, or the
   *  device is lost, so the parent (WaterfallSurface) falls back to the WebGL
   *  waterfall. */
  onUnavailable?: () => void;
};

type Status = 'probing' | 'ready' | 'unsupported';

export function WaterfallHeightfield({
  receiver = 'A',
  showStats = false,
  stitched = false,
  foreground = true,
  touchMode = 'normal',
  tuneReceiver,
  dbScale = true,
  onUnavailable,
}: Props = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const cursorRef = useRef<HTMLDivElement | null>(null);
  // Ref so the long-lived effect always calls the latest callback without
  // re-running on every parent render.
  const onUnavailableRef = useRef(onUnavailable);
  onUnavailableRef.current = onUnavailable;
  const [status, setStatus] = useState<Status>('probing');
  const [reason, setReason] = useState<string>('');
  const [stats, setStats] = useState<{ fps: number; ms: number } | null>(null);
  const rxIndex = rxIndexOf(receiver);
  const receiverFilterColor = spectrumReceiverFilterColor(receiver);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    let renderer: HeightfieldRenderer | null = null;
    let disposed = false;
    let lost = false;
    let lastSeqDrawn = -1;
    let frameAccum = 0;
    let frameCount = 0;
    let lastStatsAt = 0;
    // Signal-Pop enhance scratch (floor-subtracted 0..1 row + terrain evidence).
    let enhBuf: Float32Array | null = null;
    let terrainScratch: Float32Array | null = null;
    // Stitched dual-RX per-half floor-normalisation scratch.
    let stitchBuf: Float32Array | null = null;
    // Tracks the value domain ('pop' = 0..1, 'rx-db', 'tx-db') so a wholesale
    // domain change clears the history instead of rendering a clipped band.
    let valueDomain = '';

    // The value domain this frame would push/draw in. Pop normalises to 0..1;
    // RX and TX are dB with their own windows.
    const domainNow = (): 'pop' | 'rx-db' | 'tx-db' => {
      const { moxOn, tunOn } = useTxStore.getState();
      if (moxOn || tunOn) return 'tx-db';
      return useSignalEnhanceStore.getState().popEnabled ? 'pop' : 'rx-db';
    };

    // Upload one row. On a value-domain change first clears the history so stale
    // rows don't render as a clipped band. When Pop is on, the row is replaced by
    // the CFAR floor-subtracted 0..1 field (signal-estimator) — a flat baseline
    // so the hillshade relief lights up only real carriers (the "pull signals out
    // of the noise" path). RX/TX push the raw dB row.
    const pushFrame = (wfDb: Float32Array, centerHz: bigint, hzPerPixel: number) => {
      if (!renderer) return;
      const dom = domainNow();
      if (dom !== valueDomain) {
        valueDomain = dom;
        renderer.clearHistory();
      }
      let row = wfDb;
      // Stitched dual-RX: normalise this half's floor so the two panels join
      // seamlessly (parity with the WebGL waterfall). Skip while keyed (TX).
      if (stitched && dom !== 'tx-db') {
        const normalized = normalizeStitchedBins(row, stitchBuf, stitchFloorShiftDb(receiver, 'waterfall'));
        if (normalized !== row) stitchBuf = normalized;
        row = normalized;
      }
      if (dom === 'pop') {
        if (!enhBuf || !terrainScratch || enhBuf.length !== row.length) {
          enhBuf = new Float32Array(row.length);
          terrainScratch = new Float32Array(row.length);
        }
        enhanceInto(row, enhBuf, terrainScratch);
        row = enhBuf;
      }
      renderer.pushRow(row, Number(centerHz), hzPerPixel);
    };

    const releaseFrameConsumer = registerFrameConsumer();
    // Keep the per-bin floor estimator running so snap-to-signal works when the
    // heightfield is the active waterfall (parity with the WebGL surface).
    const releaseEstimatorConsumer = registerEstimatorConsumer();

    // Per-receiver animated view-center — RX1 and RX2/VFO B each follow their own
    // pan glide (driven by the matching Panadapter), so both stitched halves slide
    // identically. Falls back to the latest frame center until the tween inits.
    const vc = viewCenter.viewCenterFor(receiver);
    const visualCenterHz = () =>
      vc.isInitialized()
        ? vc.getViewCenterHz()
        : Number(selectDisplaySlice(useDisplayStore.getState(), receiver).centerHz);

    const resize = () => {
      const { width, height } = container.getBoundingClientRect();
      const dpr = Math.min(1, window.devicePixelRatio || 1);
      const w = Math.max(1, Math.round(width * dpr));
      const h = Math.max(1, Math.round(height * dpr));
      canvas.width = w;
      canvas.height = h;
      renderer?.resize(w, h);
    };

    const ro = new ResizeObserver(resize);

    // Visibility gating (parity with Panadapter / WebGL Waterfall): skip the GPU
    // paint while the surface is scrolled off-screen or the tab is backgrounded.
    // Frames still push into history below, so the waterfall stays continuous
    // when it returns — only the *draw* is skipped. In RX2 two heightfields are
    // mounted, so this halves idle GPU cost whenever the panel isn't visible.
    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) inViewport = e.isIntersecting;
        if (isActive()) requestRedraw();
      },
      { threshold: 0 },
    );
    const onVisibilityChange = () => {
      pageVisible = !document.hidden;
      if (isActive()) requestRedraw();
    };

    const draw = () => {
      if (!renderer || lost) return;
      // Read settings LIVE every draw so the sliders take effect immediately.
      const { wfDbMin, wfDbMax, wfTxDbMin, wfTxDbMax, waterfallScrollSpeed } =
        useDisplaySettingsStore.getState();
      const enhance = useSignalEnhanceStore.getState();
      const dom = domainNow();
      // Pop rows are normalised 0..1; RX/TX keep their dB windows. Mirrors the
      // WebGL waterfall's redraw() window selection.
      const dbMin = dom === 'pop' ? 0 : dom === 'tx-db' ? wfTxDbMin : wfDbMin;
      const dbMax = dom === 'pop' ? 1 : dom === 'tx-db' ? wfTxDbMax : wfDbMax;
      renderer.setScrollSpeed(waterfallScrollSpeed);
      renderer.setReliefDepth(Math.max(0, Math.min(1, enhance.waterfallReliefDepth / 100)));
      // Temporal de-speckle only in Pop (where floor subtraction creates speckle);
      // off for raw RX/TX. Driven by the waterfall-smoothness knob.
      renderer.setCleanup(dom === 'pop' ? Math.max(0, Math.min(1, enhance.waterfallSmoothness / 100)) : 0);
      // Pop signal-glow (crest lift + cyan-white highlight) only in Pop, driven by
      // the pop-intensity knob; 0 in normal RX so the surface stays plain hillshade.
      renderer.setPopGlow(dom === 'pop' ? Math.max(0, Math.min(1, enhance.popRenderIntensity / 100)) : 0);
      // View span: zoom is a single global setting (DspPipelineService applies the
      // same SetZoom to RX1 and RX2, and both frames carry the same hzPerPixel),
      // so BOTH halves follow the one shared zoom glide — RX2 scales as smoothly
      // as RX1 instead of stepping off its own slice. The Kiwi slice receiver is
      // the exception: it has an independent native span the shared tween knows
      // nothing about, so it self-scales to its OWN current frame Hz/pixel
      // (viewHzPerPixel == the renderer's anchor → scale 1, full width). See
      // displayedHzPerPixelFor.
      const ownFrameHzPerPixel = selectDisplaySlice(useDisplayStore.getState(), receiver).hzPerPixel;
      const viewHzPerPixel =
        rxIndex === KIWI_RECEIVER_INDEX
          ? ownFrameHzPerPixel > 0
            ? viewZoom.displayedHzPerPixelFor(rxIndex, ownFrameHzPerPixel)
            : null
          : viewZoom.isInitialized()
            ? viewZoom.getDisplayedHzPerPixel()
            : null;
      const t0 = performance.now();
      renderer.draw(dbMin, dbMax, visualCenterHz(), viewHzPerPixel);
      const dt = performance.now() - t0;
      if (showStats) {
        frameAccum += dt;
        frameCount += 1;
        if (t0 - lastStatsAt > 500) {
          const ms = frameAccum / Math.max(1, frameCount);
          setStats({ fps: Math.round(1000 / Math.max(0.001, ms)), ms: Math.round(ms * 100) / 100 });
          frameAccum = 0;
          frameCount = 0;
          lastStatsAt = t0;
        }
      }
    };

    // Coalesce every repaint trigger (new frame, glide tick, slider) onto one
    // draw per animation frame — cheaper on low-resource GPUs than redundant
    // draws, and the shared bus dedupes by callback identity.
    const requestRedraw = () => {
      if (!isActive()) return;
      requestDrawBusFrame(draw);
    };

    let unsub: (() => void) | null = null;
    let unsubSettings: (() => void) | null = null;
    let unsubViewCenter: (() => void) | null = null;
    let unsubViewZoom: (() => void) | null = null;
    let unsubTx: (() => void) | null = null;
    let unsubEnhance: (() => void) | null = null;

    const fail = (reason: string) => {
      setStatus('unsupported');
      setReason(reason);
      onUnavailableRef.current?.();
    };

    void probeWebGpu().then((probe) => {
      if (disposed) return;
      if (!probe.supported || !probe.device) {
        fail(probe.reason);
        return;
      }
      const ctx = canvas.getContext('webgpu');
      if (!ctx) {
        fail('canvas.getContext("webgpu") returned null');
        return;
      }
      try {
        renderer = createHeightfieldRenderer(probe.device, ctx, probe.format);
      } catch (err) {
        fail(`renderer init failed: ${err instanceof Error ? err.message : String(err)}`);
        return;
      }
      if (!renderer) return;
      // Device loss (GPU reset / eviction — more likely on low-resource systems)
      // → stop drawing and fall back to WebGL for this mount.
      void probe.device.lost.then((info) => {
        if (disposed || info.reason === 'destroyed') return;
        lost = true;
        resetWebGpuProbe();
        fail(`device lost: ${info.message || info.reason}`);
      });
      renderer.setColormap(useDisplaySettingsStore.getState().colormap);
      renderer.setScrollSpeed(useDisplaySettingsStore.getState().waterfallScrollSpeed);
      setStatus('ready');
      resize();
      ro.observe(container);
      io.observe(container);
      document.addEventListener('visibilitychange', onVisibilityChange);

      // Seed from the last-held frame so a paused-RX mount is not blank.
      const slice0 = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (slice0.wfValid && slice0.wfDb) {
        pushFrame(slice0.wfDb, slice0.centerHz, slice0.hzPerPixel);
        lastSeqDrawn = slice0.lastSeq;
      }
      requestRedraw();

      unsub = useDisplayStore.subscribe((state) => {
        if (!renderer || lost) return;
        const slice = selectDisplaySlice(state, receiver);
        if (slice.lastSeq === 0 || slice.lastSeq === lastSeqDrawn) return;
        lastSeqDrawn = slice.lastSeq;
        if (slice.wfValid && slice.wfDb) {
          pushFrame(slice.wfDb, slice.centerHz, slice.hzPerPixel);
        }
        requestRedraw();
      });

      // Settings → live repaint. The dB-range and scroll-speed sliders, and a
      // colormap swap, must land immediately rather than waiting for the next
      // RF frame (and on a paused band there is no next frame).
      unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
        if (!renderer || lost) return;
        if (state.colormap !== prev.colormap) renderer.setColormap(state.colormap);
        if (
          state.colormap !== prev.colormap ||
          state.wfDbMin !== prev.wfDbMin ||
          state.wfDbMax !== prev.wfDbMax ||
          state.waterfallScrollSpeed !== prev.waterfallScrollSpeed
        ) {
          requestRedraw();
        }
      });

      // Pan/zoom glide → repaint while the view eases (the per-row sampling
      // transform animates the offset/scale, so zoom and retune slide instead of
      // stepping). Silent when the tweens are parked.
      unsubViewCenter = vc.subscribe(requestRedraw);
      unsubViewZoom = viewZoom.subscribe(requestRedraw);

      // MOX/TUN and Pop flip the value domain (dB↔0..1, RX↔TX window). Clear the
      // history on the flip so old-domain rows don't render as a clipped band,
      // and repaint immediately even on a paused band.
      const onDomainMaybeChanged = () => {
        if (!renderer || lost) return;
        const dom = domainNow();
        if (dom !== valueDomain) {
          valueDomain = dom;
          renderer.clearHistory();
        }
        requestRedraw();
      };
      unsubTx = useTxStore.subscribe((s, prev) => {
        if (s.moxOn !== prev.moxOn || s.tunOn !== prev.tunOn) onDomainMaybeChanged();
      });
      unsubEnhance = useSignalEnhanceStore.subscribe((s, prev) => {
        if (s.popEnabled !== prev.popEnabled) onDomainMaybeChanged();
        else if (
          s.waterfallReliefDepth !== prev.waterfallReliefDepth ||
          s.waterfallSmoothness !== prev.waterfallSmoothness ||
          s.popRenderIntensity !== prev.popRenderIntensity
        ) {
          requestRedraw();
        }
      });
    });

    return () => {
      disposed = true;
      unsub?.();
      unsubSettings?.();
      unsubViewCenter?.();
      unsubViewZoom?.();
      unsubTx?.();
      unsubEnhance?.();
      cancelDrawBusFrame(draw);
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      releaseFrameConsumer();
      releaseEstimatorConsumer();
      renderer?.dispose();
    };
  }, [receiver, showStats, stitched]);

  // Dial-position cursor (the vertical tuning crosshair). Mirrors Waterfall.tsx:
  // outside CTUN the dial sits at the view centre (50%); under CTUN it slides to
  // (vfo − targetCenter). Positioned imperatively off the draw bus.
  useEffect(() => {
    const vc = viewCenter.viewCenterFor(receiver);
    const update = () => {
      const cur = cursorRef.current;
      if (!cur) return;
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (!s.width || s.hzPerPixel <= 0) {
        cur.style.left = '50%';
        return;
      }
      const spanHz = s.width * s.hzPerPixel;
      const c = useConnectionStore.getState();
      const vfoHz = getReceiverVfoHz(c, receiver);
      // Dial offset from THIS receiver's animated target center, so the cursor
      // tracks the glide identically on both stitched halves.
      const dialOffsetHz = vc.isInitialized() ? vfoHz - vc.getTargetCenterHz() : 0;
      cur.style.left = `${((spanHz / 2 + dialOffsetHz) / spanHz) * 100}%`;
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = vc.subscribe(schedule);
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
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
      unsubConn();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, [receiver]);

  usePanTuneGesture(canvasRef, receiver, { touchMode, tuneReceiver, dragMode: 'ruler-pan' });

  return (
    <div
      ref={containerRef}
      style={{
        position: 'relative',
        width: '100%',
        height: '100%',
        minHeight: 0,
        background: 'var(--wf-0)',
        ['--vfo-filter-color' as string]: receiverFilterColor,
      } as CSSProperties}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      {/* One global waterfall dB scale — only RX1 (leftmost) renders it. */}
      {status === 'ready' && dbScale && rxIndex === 0 && <WfDbScale />}
      {/* Dial-position cursor on BOTH halves (RX2) — each tracks its own VFO so
          the stitched pair behaves like one waterfall with two live dials. */}
      {status === 'ready' && (
        <div ref={cursorRef} className="tuning-cursor" style={{ left: '50%', pointerEvents: 'none' }} />
      )}
      {/* Each half shows its OWN receiver's filter passband, regardless of focus,
          so RX2 displays both bandwidth markers (A on its half, B on its half). */}
      {status === 'ready' && (
        <PassbandOverlay resizable containerRef={containerRef} receiver={receiver} />
      )}
      {/* Hover filter crosshair on BOTH halves — each tracks its own RX
          geometry, and a click commits to THAT half's VFO (tuneReceiver follows
          the surface, so RX2 behaves like one continuous click-to-tune waterfall). */}
      {status === 'ready' && (
        <FilterCursorOverlay containerRef={containerRef} receiver={receiver} />
      )}
      {status === 'ready' && rxIndex === 0 && (!stitched || foreground) && (
        <NotchOverlay resizable containerRef={containerRef} />
      )}
      {status === 'unsupported' && (
        <div
          role="status"
          style={{
            position: 'absolute',
            top: 6,
            left: 6,
            right: 6,
            padding: '4px 6px',
            fontSize: 10,
            fontFamily: 'monospace',
            color: 'var(--fg-0)',
            background: 'rgba(0,0,0,0.62)',
            border: '1px solid rgba(255,255,255,0.22)',
            borderRadius: 3,
            pointerEvents: 'none',
            zIndex: 3,
          }}
          title={reason}
        >
          WebGPU unavailable — {reason}
        </div>
      )}
      {showStats && status === 'ready' && stats && (
        <div
          style={{
            position: 'absolute',
            bottom: 4,
            left: 6,
            padding: '2px 6px',
            fontSize: 10,
            fontFamily: 'monospace',
            color: 'var(--fg-0)',
            background: 'rgba(0,0,0,0.55)',
            borderRadius: 3,
            pointerEvents: 'none',
            zIndex: 2,
          }}
        >
          heightfield: {stats.ms} ms/draw · ~{stats.fps} fps
        </div>
      )}
    </div>
  );
}
