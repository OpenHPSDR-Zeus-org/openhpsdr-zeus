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

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { createWfRenderer, type WfGlCaps } from '../gl/waterfall';
import { planForFrame, resetFramePlan } from '../gl/frame-plan';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { useConnectionStore } from '../state/connection-store';
import { registerFrameConsumer, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import {
  enhanceInto,
  enhanceWaterfallTextureInto,
  registerEstimatorConsumer,
  useSignalEnhanceStore,
} from '../dsp/signal-estimator';
import * as viewCenter from '../state/view-center';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';
import type { RenderColormapId } from '../gl/colormap';
import { FilterCursorOverlay } from './FilterCursorOverlay';
import { NotchOverlay } from './NotchOverlay';
import { PassbandOverlay } from './PassbandOverlay';
import { WfDbScale } from './WfDbScale';

type WaterfallProps = {
  /** When true, noise floor fades to transparent so the QRZ-mode map shows through. */
  transparent?: boolean;
};

type WaterfallValueDomain = 'rx-db' | 'pop' | 'tx-db';

const CONTEXT_LOSS_TEARDOWN_DELAY_MS = 250;
const pendingContextLossTimers = new WeakMap<HTMLCanvasElement, number>();

export function Waterfall({ transparent = false }: WaterfallProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const cursorRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<ReturnType<typeof createWfRenderer> | null>(null);
  const popEnabled = useSignalEnhanceStore((s) => s.popEnabled);
  const popRenderIntensity = useSignalEnhanceStore((s) => s.popRenderIntensity);
  const waterfallReliefDepth = useSignalEnhanceStore((s) => s.waterfallReliefDepth);
  const waterfallSmoothness = useSignalEnhanceStore((s) => s.waterfallSmoothness);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const popActive = popEnabled && !moxOn && !tunOn;
  const popIntensityCss = Math.max(0, Math.min(1, popRenderIntensity / 100)).toFixed(2);
  const reliefDepthCss = Math.max(0, Math.min(1, waterfallReliefDepth / 100)).toFixed(2);
  const smoothnessCss = Math.max(0, Math.min(1, waterfallSmoothness / 100)).toFixed(2);
  // Live transparency, read by buildRenderer() on context-restore so a rebuild
  // mid-QRZ-mode comes back transparent rather than occluding the map (#629).
  const transparentRef = useRef(transparent);
  // GL float-texture capabilities, surfaced on-screen because the desktop
  // (WebView2) app has no reachable DevTools. Only shown when something the
  // waterfall needs is missing (#629).
  const [glCaps, setGlCaps] = useState<WfGlCaps | null>(null);
  const [glError, setGlError] = useState<string | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const pendingContextLossTimer = pendingContextLossTimers.get(canvas);
    if (pendingContextLossTimer !== undefined) {
      window.clearTimeout(pendingContextLossTimer);
      pendingContextLossTimers.delete(canvas);
    }

    // Tell the realtime client that decoded spectrum frames are needed —
    // ws-client.ts skips decodeDisplayFrame entirely when no consumer is
    // registered (all spectrum surfaces closed). Context-independent: stays a
    // single mount/unmount pair, NEVER re-invoked on context-restore (#629).
    const releaseFrameConsumer = registerFrameConsumer();
    const releaseEstimatorConsumer = registerEstimatorConsumer();

    // Mutable GL bindings so the renderer can be rebuilt after a WebGL context
    // loss (#629). On Windows/ANGLE the waterfall's float-texture context can
    // be evicted after minutes; without a rebuild path it freezes forever.
    let gl: WebGL2RenderingContext | null = null;
    let renderer: ReturnType<typeof createWfRenderer> | null = null;
    let contextLost = false;
    const currentValueDomain = (): WaterfallValueDomain => {
      const { popEnabled } = useSignalEnhanceStore.getState();
      const { moxOn, tunOn } = useTxStore.getState();
      if (moxOn || tunOn) return 'tx-db';
      return popEnabled ? 'pop' : 'rx-db';
    };
    let valueDomain = currentValueDomain();
    const isPopRenderActive = () => currentValueDomain() === 'pop';
    const clearIfValueDomainChanged = () => {
      const nextDomain = currentValueDomain();
      if (nextDomain === valueDomain) return;
      valueDomain = nextDomain;
      renderer?.clearHistory();
    };
    const applyRenderLook = () => {
      if (!renderer) return;
      const signalEnhance = useSignalEnhanceStore.getState();
      const active = isPopRenderActive();
      const intensity = active ? Math.max(0, Math.min(1, signalEnhance.popRenderIntensity / 100)) : 0;
      const reliefDepth = active ? Math.max(0, Math.min(1, signalEnhance.waterfallReliefDepth / 100)) : 0;
      const smoothness = active ? Math.max(0, Math.min(1, signalEnhance.waterfallSmoothness / 100)) : 0;
      const colormap: RenderColormapId = active ? 'pop' : useDisplaySettingsStore.getState().colormap;
      renderer.setScrollSpeed(useDisplaySettingsStore.getState().waterfallScrollSpeed);
      renderer.setPopMode(active, intensity, reliefDepth, smoothness);
      renderer.setColormap(colormap);
    };

    // (Re)acquire the context and build a fresh renderer with every GL
    // resource. Returns false if WebGL2 is unavailable. Does NOT touch the
    // frame-consumer registration. Reads colormap/transparency LIVE so a
    // rebuild lands on the operator's current state, not a stale closure.
    const buildRenderer = (): boolean => {
      try {
        const ctx = canvas.getContext('webgl2', {
          antialias: false,
          alpha: true,
          premultipliedAlpha: true,
        });
        if (!ctx) {
          console.error('WebGL2 not available');
          setGlCaps(null);
          setGlError('WebGL2 not available');
          return false;
        }
        gl = ctx;
        renderer = createWfRenderer(ctx);
        rendererRef.current = renderer;
        renderer.setTransparent(transparentRef.current);
        applyRenderLook();
        setGlCaps(renderer.caps);
        setGlError(null);
        return true;
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        console.error('[waterfall] renderer unavailable', err);
        renderer = null;
        rendererRef.current = null;
        setGlCaps(null);
        setGlError(message);
        return false;
      }
    };

    if (!buildRenderer()) {
      return () => {
        releaseFrameConsumer();
        releaseEstimatorConsumer();
        rendererRef.current = null;
      };
    }

    let lastSeqDrawn = -1;
    // Count context-restore cycles — a one-off eviction logs once; a steady
    // leak would climb, which is the signal to dig further (#629).
    let restoreCount = 0;
    // Waterfall frame tallies for the one-shot backend diagnostic below (#629).
    let wfFrames = 0;
    let wfValidFrames = 0;
    // Scratch buffer for Signal Pop. uploadRow() copies immediately
    // (texSubImage2D), so unlike the panadapter texture a single reused buffer
    // is safe — there's no deferred reference-identity dirty check here.
    let enhBuf: Float32Array | null = null;
    // Visibility gating: skip the rAF redraw when the waterfall tile is
    // scrolled offscreen or the tab is hidden. We still push frames into
    // the history texture so when visibility resumes the operator sees a
    // continuous timeline; we just don't paint to the visible surface.
    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;

    const redraw = () => {
      if (contextLost || !renderer) return;
      const { wfDbMin, wfDbMax, wfTxDbMin, wfTxDbMax } = useDisplaySettingsStore.getState();
      const { moxOn, tunOn } = useTxStore.getState();
      const keyed = moxOn || tunOn;
      const pop = useSignalEnhanceStore.getState();
      // POP history rows are normalized 0..1. Normal RX rows stay in dB space
      // with topographic signal relief, so the waterfall dB slider still owns
      // the range. Keyed/TX keeps the absolute TX dB window.
      const popOn = pop.popEnabled && !keyed;
      const popIntensity = popOn ? Math.max(0, Math.min(1, pop.popRenderIntensity / 100)) : 0;
      const reliefDepth = popOn ? Math.max(0, Math.min(1, pop.waterfallReliefDepth / 100)) : 0;
      const smoothness = popOn ? Math.max(0, Math.min(1, pop.waterfallSmoothness / 100)) : 0;
      renderer.setScrollSpeed(useDisplaySettingsStore.getState().waterfallScrollSpeed);
      // Mirror DbScale.tsx — keyed (MOX/TUN) renders the TX waterfall
      // window so the operator's RX noise-floor view stays put.
      const dbMin = popOn ? 0 : keyed ? wfTxDbMin : wfDbMin;
      const dbMax = popOn ? 1 : keyed ? wfTxDbMax : wfDbMax;
      renderer.setPopMode(popOn, popIntensity, reliefDepth, smoothness);
      renderer.draw(
        dbMin,
        dbMax,
        viewCenter.isInitialized() ? viewCenter.getViewCenterHz() : null,
      );
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
      // Clamp the WebGL backing store at DPR=1. Waterfall is typically the
      // largest GPU surface in the workspace; running it at native Retina
      // DPR pushes 4× pixel data through every composite for no visible
      // gain (the colormap is a smooth gradient and the per-row history
      // shift is integer-pixel). Same rationale as Panadapter.
      const dpr = Math.min(1, window.devicePixelRatio || 1);
      const w = Math.max(1, Math.round(width * dpr));
      const h = Math.max(1, Math.round(height * dpr));
      canvas.width = w;
      canvas.height = h;
      if (contextLost || !renderer) return;
      renderer.resize(w, h);
      requestRedraw();
    };

    const ro = new ResizeObserver(resize);
    ro.observe(container);
    resize();

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

    // WebGL context-loss recovery (#629). On Windows/ANGLE the waterfall's
    // float-texture context can be evicted after minutes of streaming; without
    // this the surface freezes forever. preventDefault() on 'lost' is MANDATORY
    // — without it the browser never fires 'restored'.
    const onContextLost = (e: Event) => {
      e.preventDefault();
      contextLost = true;
      cancelDrawBusFrame(redraw);
      console.warn('[waterfall] WebGL context lost — awaiting restore');
    };
    const onContextRestored = () => {
      restoreCount++;
      console.warn(`[waterfall] WebGL context restored (#${restoreCount}) — rebuilding`);
      if (!buildRenderer()) return;
      // Rebuilding the renderer is NOT enough: the waterfall's entire history
      // lives in now-lost GPU textures with no CPU mirror. Force the shared
      // planner to emit a 'reset' on the next frame so the textures re-seed,
      // and seed immediately from the last-held frame so a paused-RX restore
      // is not left blank.
      resetFramePlan();
      contextLost = false;
      resize();
      const st = useDisplayStore.getState();
      const wfDb = st.wfValid && st.wfDb ? st.wfDb : null;
      renderer!.pushFrame({ kind: 'reset', reason: 'first' }, wfDb, st.centerHz, st.hzPerPixel);
      requestRedraw();
    };
    canvas.addEventListener('webglcontextlost', onContextLost);
    canvas.addEventListener('webglcontextrestored', onContextRestored);

    // One-shot backend diagnostic (#629). The desktop app has no DevTools, so
    // ~6 s after mount we POST the waterfall's render state to the server log
    // (texWidth>0 means the history seeded; wfValidFrames>0 means real wf data
    // is arriving). One line per session — enough to confirm the fix headless.
    const diagTimer = window.setTimeout(() => {
      const ds = renderer?.debugState();
      void fetch('/api/diag/wf', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          caps: renderer?.caps,
          ...ds,
          wfFrames,
          wfValidFrames,
          restoreCount,
        }),
      }).catch(() => {});
    }, 6000);

    const unsub = useDisplayStore.subscribe((state) => {
      if (contextLost || !renderer) return;
      if (state.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = state.lastSeq;
      // Shared per-frame plan (issue #597): identical decision to the
      // panadapter's, computed once per seq — and the geometry (shift/reset)
      // applies even on frames whose wf payload is invalid, so the history
      // can never drift against the trace.
      const decision = planForFrame({
        seq: state.lastSeq,
        centerHz: state.centerHz,
        hzPerPixel: state.hzPerPixel,
        width: state.width,
      });
      const wfDb = state.wfValid && state.wfDb ? state.wfDb : null;
      wfFrames++;
      if (wfDb) wfValidFrames++;
      if (wfDb) {
        // No refill hold here any more (issue #597 Phase 2): rows are
        // stamped with the LO their data was captured at, so the shared
        // shift planner places them correctly even mid-retune.
        // Feed the auto-range tracker — it's a no-op when AUTO is off.
        useDisplaySettingsStore.getState().updateAutoRange(wfDb);
      }
      // RX only: substitute the per-bin floor-subtracted texture row so weak
      // coherent carriers get shape before the colormap. Gated off while keyed
      // because TX pixels are a different dB domain.
      let wfForPush = wfDb;
      if (wfDb) {
        const { moxOn, tunOn } = useTxStore.getState();
        if (!moxOn && !tunOn) {
          if (!enhBuf || enhBuf.length !== wfDb.length) enhBuf = new Float32Array(wfDb.length);
          if (useSignalEnhanceStore.getState().popEnabled) enhanceInto(wfDb, enhBuf);
          else enhanceWaterfallTextureInto(wfDb, enhBuf);
          wfForPush = enhBuf;
        }
      }
      renderer.pushFrame(decision, wfForPush, state.centerHz, state.hzPerPixel);
      requestRedraw();
    });

    // View-center motion → redraw at display rate while gliding (the
    // fractional sampling offset in draw() moves the visible window).
    const unsubViewCenter = viewCenter.subscribe(requestRedraw);

    // Repaint on dB-range or colormap changes so the WfDbScale drag and the
    // colormap swap land without waiting for the next server frame. Re-upload
    // the LUT only when the id actually changed to avoid a texImage2D per
    // tick. The prev-state diff is load-bearing: a no-selector subscribe
    // used to fire (and redraw) on every store mutation, which during
    // ordinary RX traffic pulled the waterfall rAF floor above the
    // spectrum-tick rate.
    const unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
      if (contextLost || !renderer) return;
      if (state.colormap !== prev.colormap) {
        applyRenderLook();
        requestRedraw();
        return;
      }
      if (
        state.wfDbMin !== prev.wfDbMin ||
        state.wfDbMax !== prev.wfDbMax ||
        state.wfTxDbMin !== prev.wfTxDbMin ||
        state.wfTxDbMax !== prev.wfTxDbMax ||
        state.waterfallScrollSpeed !== prev.waterfallScrollSpeed
      ) {
        if (state.waterfallScrollSpeed !== prev.waterfallScrollSpeed) {
          renderer.setScrollSpeed(state.waterfallScrollSpeed);
        }
        requestRedraw();
      }
    });

    // Repaint when MOX/TUN flips so the RX↔TX waterfall window swap lands
    // immediately instead of waiting for the next server frame or scale drag.
    // App.tsx:211 uses the same prev-state diff pattern — without it the
    // unconditional subscriber fires on every tx-store update (mic dBFS at
    // 50 Hz from the worklet) and raises the floor on redraw rate above the
    // spectrum-tick rate.
    const unsubTx = useTxStore.subscribe((state, prev) => {
      if (state.moxOn !== prev.moxOn || state.tunOn !== prev.tunOn) {
        clearIfValueDomainChanged();
        applyRenderLook();
        requestRedraw();
      }
    });

    // Signal enhancement / TX toggles: POP is normalized 0..1, normal RX and
    // TX are dB domains. Re-seed history so rows from one domain don't render
    // as clipped bands in another.
    const unsubEnhance = useSignalEnhanceStore.subscribe((state, prev) => {
      if (state.popEnabled !== prev.popEnabled) {
        clearIfValueDomainChanged();
        applyRenderLook();
        requestRedraw();
      } else if (
        state.popFloorDb !== prev.popFloorDb ||
        state.popSpanDb !== prev.popSpanDb ||
        state.popGamma !== prev.popGamma ||
        state.popRenderIntensity !== prev.popRenderIntensity ||
        state.waterfallReliefDepth !== prev.waterfallReliefDepth ||
        state.waterfallSmoothness !== prev.waterfallSmoothness ||
        state.coherenceHoldGate !== prev.coherenceHoldGate ||
        state.coherenceBoostDb !== prev.coherenceBoostDb ||
        state.ridgeBoost !== prev.ridgeBoost ||
        state.ridgeMaxBoostDb !== prev.ridgeMaxBoostDb ||
        state.visualAgcEnabled !== prev.visualAgcEnabled ||
        state.visualAgcStrength !== prev.visualAgcStrength ||
        state.impulseRejectEnabled !== prev.impulseRejectEnabled ||
        state.impulseRejectDb !== prev.impulseRejectDb
      ) {
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
      window.clearTimeout(diagTimer);
      // Remove loss/restore listeners BEFORE loseContext() — loseContext fires
      // 'webglcontextlost' synchronously, and we don't want onContextLost to
      // run during teardown.
      canvas.removeEventListener('webglcontextlost', onContextLost);
      canvas.removeEventListener('webglcontextrestored', onContextRestored);
      cancelDrawBusFrame(redraw);
      releaseFrameConsumer();
      releaseEstimatorConsumer();
      renderer?.dispose();
      // Free the ANGLE context slot on real unmounts, but give React
      // StrictMode's development-only effect remount a chance to cancel it.
      // Losing the context synchronously during that probe leaves the reused
      // canvas with a half-restored WebGL context; ANGLE then reports missing
      // extensions and shader compilation fails with an empty compiler log.
      const loseContext = gl?.getExtension('WEBGL_lose_context');
      if (loseContext) {
        const timer = window.setTimeout(() => {
          if (pendingContextLossTimers.get(canvas) !== timer) return;
          pendingContextLossTimers.delete(canvas);
          loseContext.loseContext();
        }, CONTEXT_LOSS_TEARDOWN_DELAY_MS);
        pendingContextLossTimers.set(canvas, timer);
      }
      rendererRef.current = null;
    };
  }, []);

  // Keep the renderer's transparency flag in sync without remounting so the
  // history texture survives a QRZ engage/disengage. draw() runs on the next
  // frame via the realtime store subscription.
  useEffect(() => {
    transparentRef.current = transparent;
    rendererRef.current?.setTransparent(transparent);
  }, [transparent]);

  // Dial-position cursor. Outside CTUN the dial sits on the view centre so the
  // cursor stays at 50%; under CTUN the dial roams off-centre, so we slide it
  // to (vfo − targetCenter) — the same offset the panadapter FreqAxis marker
  // uses — so both surfaces agree on where the dial is. Positioned imperatively
  // off the draw-bus to avoid a React commit per input/display frame.
  useEffect(() => {
    const update = () => {
      const cur = cursorRef.current;
      if (!cur) return;
      const s = useDisplayStore.getState();
      if (!s.width || s.hzPerPixel <= 0) {
        cur.style.left = '50%';
        return;
      }
      const spanHz = s.width * s.hzPerPixel;
      const vfoHz = useConnectionStore.getState().vfoHz;
      const dialOffsetHz = viewCenter.isInitialized()
        ? vfoHz - viewCenter.getTargetCenterHz()
        : 0;
      cur.style.left = `${((spanHz / 2 + dialOffsetHz) / spanHz) * 100}%`;
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = viewCenter.subscribe(schedule);
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (s.vfoHz !== prev.vfoHz) schedule();
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

  usePanTuneGesture(canvasRef);

  return (
    <div
      ref={containerRef}
      className={`waterfall-canvas${popActive ? ' pop-enhanced' : ''}`}
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: popActive ? 'var(--pop-surface-bg)' : 'var(--wf-0)',
        ...(popActive
          ? ({
              ['--pop-intensity' as string]: popIntensityCss,
              ['--pop-relief' as string]: reliefDepthCss,
              ['--pop-smoothness' as string]: smoothnessCss,
            } as CSSProperties)
          : undefined),
      }}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      {glCaps && !glCaps.floatLinear && (
        // On-screen diagnostic (#629). The desktop (WebView2) app has no
        // reachable DevTools, so when the GPU lacks OES_texture_float_linear —
        // the prime suspect for the Windows-in-a-VM "no waterfall" — surface it
        // here. The NEAREST fallback keeps the waterfall working; this just
        // confirms the cause from a screenshot.
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
          title={`GPU: ${glCaps.gpu}`}
        >
          wf: float_linear unsupported → NEAREST fallback
        </div>
      )}
      {glError && (
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
          title={glError}
        >
          Waterfall renderer unavailable
        </div>
      )}
      <WfDbScale />
      <div
        ref={cursorRef}
        className="tuning-cursor"
        style={{ left: '50%', pointerEvents: 'none' }}
      />
      <PassbandOverlay resizable containerRef={containerRef} />
      <FilterCursorOverlay containerRef={containerRef} />
      {/* No delete ✕ here — the single control lives on the panadapter (top). */}
      <NotchOverlay resizable containerRef={containerRef} />
    </div>
  );
}
