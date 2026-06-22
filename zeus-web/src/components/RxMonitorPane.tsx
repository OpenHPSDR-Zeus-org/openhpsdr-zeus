// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Read-only live spectrum pane for a multi-DDC RX3+ receiver (rxId >= 2). It
// reuses the proven WebGL pan renderer (gl/panadapter.ts) but with a stripped
// draw path: the server frame's own trace is drawn anchored at its own centre,
// with no view-centre glide, no zoom tween, no stitch normalisation, and no
// tuning gestures or filter/passband overlays. Those all belong to the VFO-A/B
// model that RX1/RX2 own; keeping RX3+ panes isolated means this component
// cannot perturb the primary receivers' rendering in any way.
//
// Interactive tuning/filters for RX3+ are a follow-up; today these panes are
// live monitors driven from StateDto.Receivers[] + the display store's `extra`
// slices (selectDisplaySliceByRxId).

import { useEffect, useRef, type CSSProperties } from 'react';
import {
  cancelPendingPanContextLoss,
  createPanRenderer,
  hexToRgbFloats,
  schedulePanContextLoss,
} from '../gl/panadapter';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import {
  registerFrameConsumer,
  selectDisplaySliceByRxId,
  useDisplayStore,
} from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useConnectionStore } from '../state/connection-store';

type RxMonitorPaneProps = {
  // 0-based receiver index. Intended for RX3+ (index >= 2); the rxId on the wire
  // is the same value, so the display slice is selectDisplaySliceByRxId(state, rxIndex).
  rxIndex: number;
};

export function RxMonitorPane({ rxIndex }: RxMonitorPaneProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  const receiver = useConnectionStore((s) =>
    s.receivers.find((r) => r.index === rxIndex),
  );
  const vfoHz = receiver?.vfoHz ?? 0;
  const mode = receiver?.mode ?? '';
  const adcSource = receiver?.adcSource ?? 0;

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    // Cancel any deferred context loss scheduled by a previous unmount so a
    // quick remount reuses the canvas instead of tearing down a live context
    // (mirrors Panadapter.tsx, #629).
    cancelPendingPanContextLoss(canvas);

    const gl = canvas.getContext('webgl2', {
      antialias: true,
      alpha: true,
      premultipliedAlpha: true,
    });
    if (!gl) {
      console.error('WebGL2 not available');
      return;
    }

    // Tell the realtime client decoded spectrum frames are needed — ws-client
    // skips decodeDisplayFrame entirely when no consumer is registered.
    const releaseFrameConsumer = registerFrameConsumer();
    const renderer = createPanRenderer(gl);

    // The adopted trace is simply the latest server frame for this rxId. No
    // anchor offset/scale: a monitor pane shows the captured spectrum centred,
    // it does not glide or zoom with an animated view-centre.
    let anchorPan: Float32Array | null = null;

    let inViewport = true;
    let pageVisible = !document.hidden;
    const isActive = () => inViewport && pageVisible;

    const redraw = () => {
      if (!isActive() || !anchorPan) return;
      const s = useDisplaySettingsStore.getState();
      const { r, g, b } = hexToRgbFloats(s.rxTraceColor);
      renderer.setTraceColor(r, g, b);
      renderer.setPopMode(false, 0);
      renderer.draw(anchorPan, s.dbMin, s.dbMax, 0, 1);
    };
    const requestRedraw = () => {
      if (!isActive()) return;
      requestDrawBusFrame(redraw);
    };

    const resize = () => {
      const { width, height } = container.getBoundingClientRect();
      // Clamp the backing store at DPR=1 — see Panadapter.tsx resize() for why
      // sub-pixel AA on a single-pixel trace isn't worth the GPU cost.
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

    let lastSeqDrawn = -1;
    const unsub = useDisplayStore.subscribe((state) => {
      const slice = selectDisplaySliceByRxId(state, rxIndex);
      if (slice.lastSeq === 0 || slice.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = slice.lastSeq;
      if (slice.panValid && slice.panDb) anchorPan = slice.panDb;
      requestRedraw();
    });

    // Repaint on dB-range / trace-colour changes without waiting for a frame.
    const unsubSettings = useDisplaySettingsStore.subscribe((state, prev) => {
      if (
        state.dbMin !== prev.dbMin ||
        state.dbMax !== prev.dbMax ||
        state.rxTraceColor !== prev.rxTraceColor
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
      schedulePanContextLoss(canvas, gl);
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
      <div
        className="pointer-events-none absolute z-[25] rounded-sm px-2 py-0.5 font-mono text-[10px]"
        style={{
          top: 6,
          left: 8,
          background: 'rgba(8, 10, 14, 0.78)',
          color: 'var(--accent, #4a9eff)',
          border: '1px solid rgba(255,255,255,0.16)',
        }}
      >
        {`RX${rxIndex + 1}`}
        {mode ? ` · ${mode}` : ''} · {(vfoHz / 1e6).toFixed(6)} · ADC{adcSource}
      </div>
    </div>
  );
}
