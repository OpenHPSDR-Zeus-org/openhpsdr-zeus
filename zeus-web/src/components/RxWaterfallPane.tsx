// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Read-only live WATERFALL pane for a multi-DDC RX3+ receiver (rxId >= 2). The
// sibling RxMonitorPane draws the panadapter trace; this draws the scrolling
// waterfall history for the same receiver. It reuses the production waterfall GL
// renderer (gl/waterfall.ts) and the shared per-frame shift planner, but with a
// stripped draw path: the receiver's own frames scroll anchored at their own
// centre — no view-centre glide, no zoom tween, no stitch normalisation, and no
// tuning gestures or overlays (those belong to the VFO-A/B model RX1/RX2 own).
// Keeping RX3+ panes isolated means they cannot perturb the primary receivers.

import { useEffect, useRef, type CSSProperties } from 'react';
import { createWfRenderer } from '../gl/waterfall';
import { planForFrame, resetFramePlan } from '../gl/frame-plan';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import {
  registerFrameConsumer,
  selectDisplaySliceByRxId,
  useDisplayStore,
} from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';

type RxWaterfallPaneProps = {
  // 0-based receiver index (intended for RX3+, index >= 2). The wire rxId equals
  // this, so the display slice is selectDisplaySliceByRxId(state, rxIndex).
  rxIndex: number;
};

export function RxWaterfallPane({ rxIndex }: RxWaterfallPaneProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const gl = canvas.getContext('webgl2', {
      antialias: false,
      alpha: true,
      premultipliedAlpha: true,
    });
    if (!gl) {
      console.error('WebGL2 not available');
      return;
    }

    const releaseFrameConsumer = registerFrameConsumer();
    const renderer = createWfRenderer(gl);
    // Unique plan key so this pane's shift history never collides with RX1/RX2
    // ('A'/'B') or another extra receiver.
    const planKey = `rx${rxIndex}`;
    resetFramePlan(planKey);

    const settings0 = useDisplaySettingsStore.getState();
    renderer.setColormap(settings0.colormap);
    renderer.setScrollSpeed(settings0.waterfallScrollSpeed);
    renderer.setTransparent(false);

    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;

    const redraw = () => {
      if (!isActive()) return;
      const s = useDisplaySettingsStore.getState();
      renderer.draw(s.wfDbMin, s.wfDbMax);
    };
    const requestRedraw = () => {
      if (!isActive()) return;
      requestDrawBusFrame(redraw);
    };

    const resize = () => {
      const { width, height } = container.getBoundingClientRect();
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

    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries) inViewport = e.isIntersecting;
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

    let lastSeqPushed = -1;
    const unsub = useDisplayStore.subscribe((state) => {
      const slice = selectDisplaySliceByRxId(state, rxIndex);
      if (slice.lastSeq === 0 || slice.lastSeq === lastSeqPushed) return;
      lastSeqPushed = slice.lastSeq;
      // Shared shift planner keyed to this receiver — geometry (shift/reset)
      // applies even when the wf payload is invalid, so the history stays aligned.
      const decision = planForFrame({
        seq: slice.lastSeq,
        centerHz: slice.centerHz,
        hzPerPixel: slice.hzPerPixel,
        width: slice.width,
        planKey,
      });
      const wfDb = slice.wfValid && slice.wfDb ? slice.wfDb : null;
      renderer.pushFrame(decision, wfDb, slice.centerHz, slice.hzPerPixel);
      requestRedraw();
    });

    // Repaint / re-tune on dB-range, colormap, and scroll-speed changes.
    const unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
      if (state.colormap !== prev.colormap) renderer.setColormap(state.colormap);
      if (state.waterfallScrollSpeed !== prev.waterfallScrollSpeed)
        renderer.setScrollSpeed(state.waterfallScrollSpeed);
      if (
        state.wfDbMin !== prev.wfDbMin ||
        state.wfDbMax !== prev.wfDbMax ||
        state.colormap !== prev.colormap
      ) {
        requestRedraw();
      }
    });

    return () => {
      unsub();
      unsubSettings();
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibilityChange);
      cancelDrawBusFrame(redraw);
      renderer.dispose();
      resetFramePlan(planKey);
      releaseFrameConsumer();
    };
  }, [rxIndex]);

  return (
    <div
      ref={containerRef}
      className="spectrum-canvas"
      style={
        {
          position: 'relative',
          minHeight: 0,
          width: '100%',
          height: '100%',
          background: 'var(--spec-bg)',
        } as CSSProperties
      }
    >
      <canvas
        ref={canvasRef}
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}
      />
    </div>
  );
}
