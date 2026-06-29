// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement, useRef } from 'react';

import { act, render } from '../components/meters/__tests__/harness';

const setVfoMock = vi.hoisted(() => vi.fn());
const setVfoBMock = vi.hoisted(() => vi.fn());
const setZoomMock = vi.hoisted(() => vi.fn());
const setRadioLoMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setVfo: setVfoMock,
    setVfoB: setVfoBMock,
    setZoom: setZoomMock,
    setRadioLo: setRadioLoMock,
  };
});

import { useConnectionStore } from '../state/connection-store';
import { createEmptyDisplaySlice, useDisplayStore } from '../state/display-store';
import * as viewCenter from '../state/view-center';
import { maybeUpdateEstimator, resetEstimator, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';
import {
  _resetPanSnapStickyForTest,
  resolvePanTuneTarget,
  usePanTuneGesture,
  type PanTuneGestureOptions,
} from './use-pan-tune-gesture';
import { useVfoLockStore } from '../state/vfo-lock-store';
import type { RadioStateDto, RxMode } from '../api/client';

const SNAP_WIDTH = 256;
const SNAP_HZ_PER_PX = 37;
const SNAP_CENTER = 14_200_000;
const SNAP_NOISE_DB = -110;
let rafNowMs = 0;
let nextRafHandle = 1;
let rafCallbacks = new Map<number, FrameRequestCallback>();

function binHz(bin: number): number {
  return SNAP_CENTER + (bin - SNAP_WIDTH / 2) * SNAP_HZ_PER_PX;
}

function voiceBlock(): Float32Array {
  const spec = new Float32Array(SNAP_WIDTH).fill(SNAP_NOISE_DB);
  for (let i = 140; i <= 160; i++) spec[i] = -60 - Math.abs(i - 150) * 0.35;
  return spec;
}

// Two closely-spaced carriers — signal A (bins 100..110) and signal B (bins
// 130..140) with a clear noise gap between them — for exercising snap hysteresis.
function twoSignals(): Float32Array {
  const spec = new Float32Array(SNAP_WIDTH).fill(SNAP_NOISE_DB);
  for (let i = 100; i <= 110; i++) spec[i] = -60 - Math.abs(i - 105) * 0.4;
  for (let i = 130; i <= 140; i++) spec[i] = -60 - Math.abs(i - 135) * 0.4;
  return spec;
}

function pushSnapFrame(spec: Float32Array, seq = 1): void {
  maybeUpdateEstimator({ panDb: spec, panValid: true, width: SNAP_WIDTH, hzPerPixel: SNAP_HZ_PER_PX });
  useDisplayStore.setState({
    width: SNAP_WIDTH,
    centerHz: BigInt(SNAP_CENTER),
    hzPerPixel: SNAP_HZ_PER_PX,
    panDb: spec,
    panValid: true,
    lastSeq: seq,
  });
}

function GestureProbe({
  touchMode,
  receiver = 'A',
  tuneReceiver,
  dragMode,
}: {
  touchMode: PanTuneGestureOptions['touchMode'];
  receiver?: 'A' | 'B';
  tuneReceiver?: PanTuneGestureOptions['tuneReceiver'];
  dragMode?: PanTuneGestureOptions['dragMode'];
}) {
  const ref = useRef<HTMLCanvasElement | null>(null);
  usePanTuneGesture(ref, receiver, { touchMode, tuneReceiver, dragMode });
  return createElement('canvas', { ref });
}

function pointer(
  target: HTMLCanvasElement,
  type: string,
  init: {
    pointerId: number;
    clientX: number;
    clientY?: number;
    pointerType?: string;
    button?: number;
  },
): void {
  const ev = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperties(ev, {
    pointerId: { value: init.pointerId },
    clientX: { value: init.clientX },
    clientY: { value: init.clientY ?? 0 },
    pointerType: { value: init.pointerType ?? 'touch' },
    button: { value: init.button ?? 0 },
  });
  target.dispatchEvent(ev);
}

function wheel(
  target: HTMLCanvasElement,
  init: {
    deltaY?: number;
    deltaX?: number;
    deltaMode?: number;
    shiftKey?: boolean;
    altKey?: boolean;
  },
): void {
  const ev = new Event('wheel', { bubbles: true, cancelable: true });
  Object.defineProperties(ev, {
    deltaY: { value: init.deltaY ?? 0 },
    deltaX: { value: init.deltaX ?? 0 },
    deltaMode: { value: init.deltaMode ?? 0 },
    shiftKey: { value: init.shiftKey ?? false },
    altKey: { value: init.altKey ?? false },
  });
  target.dispatchEvent(ev);
}

async function flush(): Promise<void> {
  drainRafs();
  await Promise.resolve();
  drainRafs();
  await Promise.resolve();
  drainRafs();
}

function drainRafs(maxFrames = 1000): void {
  let frames = 0;
  while (rafCallbacks.size > 0 && frames < maxFrames) {
    const callbacks = Array.from(rafCallbacks.values());
    rafCallbacks.clear();
    rafNowMs += 16.7;
    for (const cb of callbacks) cb(rafNowMs);
    frames++;
  }
}

function currentRadioState(overrides: Partial<RadioStateDto> = {}): RadioStateDto {
  return {
    ...useConnectionStore.getState(),
    ...overrides,
  } as RadioStateDto;
}

// RX2 lives in receivers[1]. Helpers to seed and read it in tests.
function rxEntry(index: number, vfoHz: number) {
  return {
    index, enabled: true, adcSource: 0, vfoHz, mode: 'USB' as RxMode,
    filterLowHz: 100, filterHighHz: 2800, filterPresetName: 'VAR1', afGainDb: 0,
    sampleRateHz: 192_000, muted: false,
  };
}
function rx2Vfo(): number | undefined {
  return useConnectionStore.getState().receivers.find((r) => r.index === 1)?.vfoHz;
}

describe('usePanTuneGesture mobile touch mode', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    viewCenter._resetForTest();
    rafNowMs = 0;
    nextRafHandle = 1;
    rafCallbacks = new Map<number, FrameRequestCallback>();
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn((cb: FrameRequestCallback) => {
        const handle = nextRafHandle++;
        rafCallbacks.set(handle, cb);
        return handle;
      }),
    );
    vi.stubGlobal(
      'cancelAnimationFrame',
      vi.fn((handle: number) => {
        rafCallbacks.delete(handle);
      }),
    );
    Object.defineProperty(HTMLCanvasElement.prototype, 'setPointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLCanvasElement.prototype, 'hasPointerCapture', {
      configurable: true,
      value: vi.fn(() => true),
    });
    Object.defineProperty(HTMLCanvasElement.prototype, 'releasePointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLCanvasElement.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: vi.fn(() => ({
        left: 0,
        top: 0,
        right: 200,
        bottom: 100,
        width: 200,
        height: 100,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      })),
    });

    useConnectionStore.setState({
      status: 'Connected',
      vfoHz: 14_200_000,
      receivers: [rxEntry(0, 14_200_000), rxEntry(1, 14_200_000)],
      ctunEnabled: false,
      zoomLevel: 4,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
      rx2: createEmptyDisplaySlice(),
    });
    setZoomMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
    setVfoMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
    setVfoBMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
    setRadioLoMock.mockImplementation(async () => ({ ...useConnectionStore.getState() }));
  });

  afterEach(() => {
    useVfoLockStore.setState({ locked: false });
    viewCenter._resetForTest();
    resetEstimator();
    _resetPanSnapStickyForTest();
    useSignalEnhanceStore.setState({
      popEnabled: false,
      snapEnabled: false,
      autoNotchEnabled: false,
      visualAgcEnabled: true,
      impulseRejectEnabled: true,
    });
    useSignalEnhanceStore.getState().resetSignalEnhanceTuning();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('does not tune from a single touch when touch mode is pinch-only', async () => {
    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'pinch-only' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 80 });
      pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150 });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150 });
      await flush();
    });

    expect(setVfoMock).not.toHaveBeenCalled();
    expect(setVfoBMock).not.toHaveBeenCalled();
    expect(setZoomMock).not.toHaveBeenCalled();

    unmount();
  });

  it('still zooms from a two-finger pinch when touch mode is pinch-only', async () => {
    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'pinch-only' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, clientY: 0 });
      pointer(canvas, 'pointerdown', { pointerId: 2, clientX: 140, clientY: 0 });
      pointer(canvas, 'pointermove', { pointerId: 2, clientX: 180, clientY: 0 });
      await flush();
    });

    expect(setVfoMock).not.toHaveBeenCalled();
    expect(setVfoBMock).not.toHaveBeenCalled();
    expect(setZoomMock).toHaveBeenCalledWith(8, expect.any(AbortSignal));

    unmount();
  });

  it('quantizes the snap-mode hover target (USB low edge) onto the toolbar tuning step', () => {
    useConnectionStore.setState({ mode: 'USB' });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 5000 });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    // Signal occupies bins 140..160; USB tunes the low edge (bin 140). With a
    // 5 kHz step the edge frequency is rounded onto the grid so the preview
    // holds a stable value instead of chasing the breathing edge.
    const target = resolvePanTuneTarget(binHz(152));

    expect(target.snappedToSignal).toBe(true);
    expect(target.fromLive).toBe(true);
    expect(target.tuneHz).toBe(Math.round(binHz(140) / 5000) * 5000);
    expect(target.tuneHz % 5000).toBe(0);
  });

  it('snaps to a finer step grid when the operator picks a small tuning step', () => {
    useConnectionStore.setState({ mode: 'USB' });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 100 });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    const target = resolvePanTuneTarget(binHz(152));

    expect(target.snappedToSignal).toBe(true);
    expect(target.tuneHz).toBe(Math.round(binHz(140) / 100) * 100);
    expect(target.tuneHz % 100).toBe(0);
  });

  it('holds the snapped signal between two close carriers, switching only when the cursor reaches the other', () => {
    useConnectionStore.setState({ mode: 'USB' });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 1 });
    const spec = twoSignals();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    // Lock onto signal A (USB low edge at bin 100).
    const onA = resolvePanTuneTarget(binHz(105));
    expect(onA.tuneHz).toBe(Math.round(binHz(100)));

    // Cursor drifts into the gap and is now NEARER signal B (bin 124: 222 Hz to
    // B vs 518 Hz to A) — but hysteresis keeps the snap on A, no flip-flop.
    const stillA = resolvePanTuneTarget(binHz(124));
    expect(stillA.tuneHz).toBe(Math.round(binHz(100)));

    // Cursor reaches signal B's body — now the snap switches cleanly to B.
    const onB = resolvePanTuneTarget(binHz(135));
    expect(onB.tuneHz).toBe(Math.round(binHz(130)));

    // And once on B it stays on B back across the midpoint (symmetric hold).
    const stillB = resolvePanTuneTarget(binHz(116));
    expect(stillB.tuneHz).toBe(Math.round(binHz(130)));
  });

  it('click snap posts the same step-quantized signal target that hover resolves', async () => {
    useConnectionStore.setState({ mode: 'USB', ctunEnabled: true });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 5000 });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    const hoverTarget = resolvePanTuneTarget(binHz(152));

    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;
    const clickX = (152 / SNAP_WIDTH) * 200;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: clickX, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: clickX, pointerType: 'mouse' });
      await flush();
    });

    // Click commits exactly what hover previewed — both go through the shared
    // resolver, so they cannot advertise one frequency and tune another.
    expect(setVfoMock).toHaveBeenCalledWith(hoverTarget.tuneHz, undefined);

    unmount();
  });

  it('lets focused VFO A tune from the RX2 stitched surface geometry in CTUN', async () => {
    useConnectionStore.setState({
      mode: 'USB',
      ctunEnabled: true,
      vfoHz: 14_200_000,
      receivers: [rxEntry(0, 14_200_000), rxEntry(1, 7_200_000)],
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
      rx2: {
        ...createEmptyDisplaySlice(),
        width: 200,
        centerHz: 7_200_000n,
        hzPerPixel: 100,
        lastSeq: 2,
      },
    });

    const { container, unmount } = render(
      createElement(GestureProbe, {
        touchMode: 'normal',
        receiver: 'B',
        tuneReceiver: 'A',
      }),
    );
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      await flush();
    });

    expect(setVfoMock).toHaveBeenCalledWith(7_205_000, undefined);
    expect(setVfoBMock).not.toHaveBeenCalled();

    unmount();
  });

  it('recenters CTUN zoom-in on the tuned RX1 frequency without leaving CTUN', async () => {
    const startLoHz = 14_200_000;
    const tunedHz = 14_205_000;
    useConnectionStore.setState({
      mode: 'USB',
      ctunEnabled: true,
      vfoHz: tunedHz,
      radioLoHz: startLoHz,
      zoomLevel: 4,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: BigInt(startLoHz),
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });

    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      wheel(canvas, { deltaY: 40, shiftKey: true });
      await flush();
    });

    expect(setRadioLoMock).toHaveBeenCalledWith(tunedHz, expect.any(AbortSignal));
    expect(setZoomMock).toHaveBeenCalledWith(5, expect.any(AbortSignal));
    expect(useConnectionStore.getState().vfoHz).toBe(tunedHz);
    expect(useConnectionStore.getState().radioLoHz).toBe(tunedHz);
    expect(useConnectionStore.getState().ctunEnabled).toBe(true);
    expect(viewCenter.viewCenterFor('A').getTargetCenterHz()).toBe(tunedHz);

    unmount();
  });

  it('uses the CW effective LO when recentering CTUN zoom-in', async () => {
    useConnectionStore.setState({
      mode: 'CWU',
      cwPitchHz: 700,
      ctunEnabled: true,
      vfoHz: 14_205_000,
      radioLoHz: 14_200_000,
      zoomLevel: 4,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });

    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      wheel(canvas, { deltaY: 40, shiftKey: true });
      await flush();
    });

    expect(setRadioLoMock).toHaveBeenCalledWith(14_204_300, expect.any(AbortSignal));
    expect(useConnectionStore.getState().radioLoHz).toBe(14_204_300);

    unmount();
  });

  it('drags the RX2 surface by posting VFO B instead of VFO A', async () => {
    useConnectionStore.setState({
      ctunEnabled: false,
      vfoHz: 14_200_000,
      receivers: [rxEntry(0, 14_200_000), rxEntry(1, 7_200_000)],
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
      rx2: {
        ...createEmptyDisplaySlice(),
        width: 200,
        centerHz: 7_200_000n,
        hzPerPixel: 100,
        lastSeq: 2,
      },
    });

    const { container, unmount } = render(
      createElement(GestureProbe, {
        touchMode: 'normal',
        receiver: 'B',
        tuneReceiver: 'B',
      }),
    );
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
      pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      await flush();
    });

    expect(setVfoMock).not.toHaveBeenCalled();
    expect(setVfoBMock).toHaveBeenLastCalledWith(7_195_000, undefined);
    expect(rx2Vfo()).toBe(7_195_000);

    unmount();
  });

  it('can drag-pan RX1 by posting radio LO instead of VFO A', async () => {
    useConnectionStore.setState({
      ctunEnabled: false,
      vfoHz: 14_205_000,
      radioLoHz: 14_200_000,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });

    const { container, unmount } = render(
      createElement(GestureProbe, {
        touchMode: 'normal',
        dragMode: 'ruler-pan',
      }),
    );
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
      pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      await flush();
    });

    expect(setVfoMock).not.toHaveBeenCalled();
    expect(setRadioLoMock).toHaveBeenLastCalledWith(14_195_000, expect.any(AbortSignal));
    expect(useConnectionStore.getState().vfoHz).toBe(14_205_000);
    expect(useConnectionStore.getState().radioLoHz).toBe(14_195_000);

    unmount();
  });

  it('still click-tunes when ruler-pan drag mode does not move past slop', async () => {
    useConnectionStore.setState({
      ctunEnabled: false,
      vfoHz: 14_200_000,
      radioLoHz: 14_200_000,
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
    });

    const { container, unmount } = render(
      createElement(GestureProbe, {
        touchMode: 'normal',
        dragMode: 'ruler-pan',
      }),
    );
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      await flush();
    });

    expect(setRadioLoMock).not.toHaveBeenCalled();
    expect(setVfoMock).toHaveBeenCalledWith(14_205_000, undefined);

    unmount();
  });

  it('keeps optimistic VFO B during stale untrusted state polls', async () => {
    useConnectionStore.setState({
      ctunEnabled: false,
      vfoHz: 14_200_000,
      receivers: [rxEntry(0, 14_200_000), rxEntry(1, 7_200_000)],
    });
    useDisplayStore.setState({
      width: 200,
      centerHz: 14_200_000n,
      hzPerPixel: 100,
      panDb: new Float32Array(200),
      panValid: true,
      rx2: {
        ...createEmptyDisplaySlice(),
        width: 200,
        centerHz: 7_200_000n,
        hzPerPixel: 100,
        lastSeq: 2,
      },
    });

    const { container, unmount } = render(
      createElement(GestureProbe, {
        touchMode: 'normal',
        receiver: 'B',
        tuneReceiver: 'B',
      }),
    );
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
      pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
      await flush();
    });

    // Stale poll: the server still reports RX2 at the pre-drag 7_200_000 in
    // receivers[1]. The poll guard must keep the optimistic 7_195_000.
    useConnectionStore.getState().applyState(
      currentRadioState({
        vfoHz: 14_200_000,
        receivers: [rxEntry(0, 14_200_000), rxEntry(1, 7_200_000)],
      }),
      { trustVfo: false },
    );

    expect(rx2Vfo()).toBe(7_195_000);

    pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
    unmount();
  });

  // VFO lock — issue #644 / Doug's report: a locked dial must block EVERY way to
  // change the operating frequency, not just the digit display. These prove the
  // gesture is swallowed at the source (no setVfo/setRadioLo POST AND no
  // optimistic store write), while display-only zoom/pan stay live.
  describe('with the VFO lock engaged', () => {
    beforeEach(() => {
      useVfoLockStore.setState({ locked: true });
    });

    it('swallows panadapter click-to-tune', async () => {
      useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_200_000 });
      const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      await act(async () => {
        pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        await flush();
      });

      expect(setVfoMock).not.toHaveBeenCalled();
      expect(useConnectionStore.getState().vfoHz).toBe(14_200_000);
      unmount();
    });

    it('swallows a panadapter drag-to-tune', async () => {
      useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_200_000 });
      const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      await act(async () => {
        pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
        pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        await flush();
      });

      expect(setVfoMock).not.toHaveBeenCalled();
      expect(useConnectionStore.getState().vfoHz).toBe(14_200_000);
      unmount();
    });

    it('swallows wheel-tune but still allows shift-wheel zoom', async () => {
      useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_200_000, zoomLevel: 4 });
      useToolbarFavoritesStore.setState({ stepHz: 1000 });
      const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      await act(async () => {
        wheel(canvas, { deltaY: 120 });
        await flush();
      });
      expect(setVfoMock).not.toHaveBeenCalled();
      expect(useConnectionStore.getState().vfoHz).toBe(14_200_000);

      // Zoom is display-only — the lock must not freeze it.
      await act(async () => {
        wheel(canvas, { deltaY: 120, shiftKey: true });
        await flush();
      });
      expect(setZoomMock).toHaveBeenCalled();
      unmount();
    });

    it('swallows a CTUN-off ruler pan that would move the dial (radio LO)', async () => {
      useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_205_000, radioLoHz: 14_200_000 });
      const { container, unmount } = render(
        createElement(GestureProbe, { touchMode: 'normal', dragMode: 'ruler-pan' }),
      );
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      await act(async () => {
        pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
        pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        await flush();
      });

      expect(setRadioLoMock).not.toHaveBeenCalled();
      expect(useConnectionStore.getState().radioLoHz).toBe(14_200_000);
      unmount();
    });

    it('freezes even a CTUN-on display pan (whole view is locked)', async () => {
      // KB2UKA's call: a lock freezes the panadapter entirely, so a CTUN-on
      // ruler pan that only moves the window (not the dial) is blocked too.
      useConnectionStore.setState({ ctunEnabled: true, vfoHz: 14_205_000, radioLoHz: 14_200_000 });
      const { container, unmount } = render(
        createElement(GestureProbe, { touchMode: 'normal', dragMode: 'ruler-pan' }),
      );
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      await act(async () => {
        pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 100, pointerType: 'mouse' });
        pointer(canvas, 'pointermove', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        await flush();
      });

      expect(setRadioLoMock).not.toHaveBeenCalled();
      expect(useConnectionStore.getState().radioLoHz).toBe(14_200_000);
      unmount();
    });

    it('resumes tuning the moment the lock is released', async () => {
      useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_200_000 });
      const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
      const canvas = container.querySelector('canvas') as HTMLCanvasElement;

      useVfoLockStore.setState({ locked: false });
      await act(async () => {
        pointer(canvas, 'pointerdown', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        pointer(canvas, 'pointerup', { pointerId: 1, clientX: 150, pointerType: 'mouse' });
        await flush();
      });

      expect(setVfoMock).toHaveBeenCalled();
      unmount();
    });
  });
});
