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

import { useEffect, useRef, useState } from 'react';
import { COLORMAPS } from '../gl/colormap';
import { createWfRenderer, type WfGlCaps } from '../gl/waterfall';
import { planForFrame, resetFramePlan } from '../gl/frame-plan';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import { registerFrameConsumer, useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import * as viewCenter from '../state/view-center';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';
import { WfDbScale } from './WfDbScale';

// Throttle row uploads so the waterfall scrolls at ~(server tick / N).
// With a 30 Hz server tick N=2 gives ~15 Hz, which is a comfortable scroll
// speed without costing much CPU. Shift/reset still run every frame so VFO
// retunes stay synchronised with the panadapter's offset.
// TODO(phase-3.1): expose as a UI setting.
const WF_PUSH_EVERY_N = 2;

type WaterfallProps = {
  /** When true, noise floor fades to transparent so the QRZ-mode map shows through. */
  transparent?: boolean;
};

export function Waterfall({ transparent = false }: WaterfallProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rendererRef = useRef<ReturnType<typeof createWfRenderer> | null>(null);
  // Live transparency, read by buildRenderer() on context-restore so a rebuild
  // mid-QRZ-mode comes back transparent rather than occluding the map (#629).
  const transparentRef = useRef(transparent);
  // GL float-texture capabilities, surfaced on-screen because the desktop
  // (WebView2) app has no reachable DevTools. Only shown when something the
  // waterfall needs is missing (#629).
  const [glCaps, setGlCaps] = useState<WfGlCaps | null>(null);
  const autoRange = useDisplaySettingsStore((s) => s.autoRange);
  const setAutoRange = useDisplaySettingsStore((s) => s.setAutoRange);
  const colormap = useDisplaySettingsStore((s) => s.colormap);
  const setColormap = useDisplaySettingsStore((s) => s.setColormap);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    // Tell the realtime client that decoded spectrum frames are needed —
    // ws-client.ts skips decodeDisplayFrame entirely when no consumer is
    // registered (all spectrum surfaces closed). Context-independent: stays a
    // single mount/unmount pair, NEVER re-invoked on context-restore (#629).
    const releaseFrameConsumer = registerFrameConsumer();

    // Mutable GL bindings so the renderer can be rebuilt after a WebGL context
    // loss (#629). On Windows/ANGLE the waterfall's float-texture context can
    // be evicted after minutes; without a rebuild path it freezes forever.
    let gl: WebGL2RenderingContext | null = null;
    let renderer: ReturnType<typeof createWfRenderer> | null = null;
    let contextLost = false;

    // (Re)acquire the context and build a fresh renderer with every GL
    // resource. Returns false if WebGL2 is unavailable. Does NOT touch the
    // frame-consumer registration. Reads colormap/transparency LIVE so a
    // rebuild lands on the operator's current state, not a stale closure.
    const buildRenderer = (): boolean => {
      const ctx = canvas.getContext('webgl2', {
        antialias: false,
        alpha: true,
        premultipliedAlpha: true,
      });
      if (!ctx) {
        console.error('WebGL2 not available');
        return false;
      }
      gl = ctx;
      renderer = createWfRenderer(ctx);
      rendererRef.current = renderer;
      renderer.setColormap(useDisplaySettingsStore.getState().colormap);
      renderer.setTransparent(transparentRef.current);
      setGlCaps(renderer.caps);
      return true;
    };

    if (!buildRenderer()) return;

    let lastSeqDrawn = -1;
    let tickCounter = 0;
    // Count context-restore cycles — a one-off eviction logs once; a steady
    // leak would climb, which is the signal to dig further (#629).
    let restoreCount = 0;
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
      // Mirror DbScale.tsx — keyed (MOX/TUN) renders the TX waterfall
      // window so the operator's RX noise-floor view stays put.
      const dbMin = keyed ? wfTxDbMin : wfDbMin;
      const dbMax = keyed ? wfTxDbMax : wfDbMax;
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
      let skipRowUpload = false;
      if (wfDb) {
        tickCounter++;
        skipRowUpload = tickCounter % WF_PUSH_EVERY_N !== 0;
        // No refill hold here any more (issue #597 Phase 2): rows are
        // stamped with the LO their data was captured at, so the shared
        // shift planner places them correctly even mid-retune.
        // Feed the auto-range tracker — it's a no-op when AUTO is off.
        useDisplaySettingsStore.getState().updateAutoRange(wfDb);
      }
      renderer.pushFrame(decision, wfDb, state.centerHz, state.hzPerPixel, {
        skipRowUpload,
      });
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
        renderer.setColormap(state.colormap);
        requestRedraw();
        return;
      }
      if (
        state.wfDbMin !== prev.wfDbMin ||
        state.wfDbMax !== prev.wfDbMax ||
        state.wfTxDbMin !== prev.wfTxDbMin ||
        state.wfTxDbMax !== prev.wfTxDbMax
      ) {
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
        requestRedraw();
      }
    });

    return () => {
      unsub();
      unsubViewCenter();
      unsubSettings();
      unsubTx();
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      // Remove loss/restore listeners BEFORE loseContext() — loseContext fires
      // 'webglcontextlost' synchronously, and we don't want onContextLost to
      // run during teardown.
      canvas.removeEventListener('webglcontextlost', onContextLost);
      canvas.removeEventListener('webglcontextrestored', onContextRestored);
      cancelDrawBusFrame(redraw);
      releaseFrameConsumer();
      renderer?.dispose();
      // Free the ANGLE context slot now rather than waiting for GC — on a
      // disconnect/reconnect cycle the Waterfall remounts, and ANGLE caps the
      // number of live WebGL contexts (#629).
      gl?.getExtension('WEBGL_lose_context')?.loseContext();
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

  usePanTuneGesture(canvasRef);

  return (
    <div
      ref={containerRef}
      className="waterfall-canvas"
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: 'var(--wf-0)',
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
      <WfDbScale />
      <div
        className="tuning-cursor"
        style={{ left: '50%', pointerEvents: 'none' }}
      />
      <div style={{ position: 'absolute', top: 6, right: 6, display: 'flex', alignItems: 'center', gap: 4 }}>
        <div role="radiogroup" aria-label="Colormap" className="btn-row">
          {COLORMAPS.map((cm) => {
            const active = colormap === cm.id;
            return (
              <button
                key={cm.id}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => setColormap(cm.id)}
                title={`Waterfall colormap: ${cm.label}`}
                className={`btn sm ${active ? 'active' : ''}`}
              >
                {cm.label}
              </button>
            );
          })}
        </div>
        <button
          type="button"
          onClick={() => setAutoRange(!autoRange)}
          aria-pressed={autoRange}
          title={
            autoRange
              ? 'Auto dB range: tracking p5/p95 of waterfall samples'
              : 'Fixed dB range: −120 to −30 dBFS'
          }
          className={`btn sm ${autoRange ? 'active' : ''}`}
        >
          {autoRange ? 'dB: AUTO' : 'dB: FIXED'}
        </button>
      </div>
    </div>
  );
}
