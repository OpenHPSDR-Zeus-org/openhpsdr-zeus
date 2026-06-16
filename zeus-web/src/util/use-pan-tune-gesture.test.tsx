// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement, useRef } from 'react';

import { act, render } from '../components/meters/__tests__/harness';

const setVfoMock = vi.hoisted(() => vi.fn());
const setVfoBMock = vi.hoisted(() => vi.fn());
const setZoomMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setVfo: setVfoMock,
    setVfoB: setVfoBMock,
    setZoom: setZoomMock,
  };
});

import { useConnectionStore } from '../state/connection-store';
import { createEmptyDisplaySlice, useDisplayStore } from '../state/display-store';
import { maybeUpdateEstimator, resetEstimator, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';
import { resolvePanTuneTarget, usePanTuneGesture, type PanTuneGestureOptions } from './use-pan-tune-gesture';

const SNAP_WIDTH = 256;
const SNAP_HZ_PER_PX = 37;
const SNAP_CENTER = 14_200_000;
const SNAP_NOISE_DB = -110;

function binHz(bin: number): number {
  return SNAP_CENTER + (bin - SNAP_WIDTH / 2) * SNAP_HZ_PER_PX;
}

function voiceBlock(): Float32Array {
  const spec = new Float32Array(SNAP_WIDTH).fill(SNAP_NOISE_DB);
  for (let i = 140; i <= 160; i++) spec[i] = -60 - Math.abs(i - 150) * 0.35;
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
}: {
  touchMode: PanTuneGestureOptions['touchMode'];
  receiver?: 'A' | 'B';
  tuneReceiver?: PanTuneGestureOptions['tuneReceiver'];
}) {
  const ref = useRef<HTMLCanvasElement | null>(null);
  usePanTuneGesture(ref, receiver, { touchMode, tuneReceiver });
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

async function flush(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe('usePanTuneGesture mobile touch mode', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn((cb: FrameRequestCallback) => {
        cb(0);
        return 1;
      }),
    );
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
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
      vfoBHz: 14_200_000,
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
  });

  afterEach(() => {
    resetEstimator();
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

  it('resolves snap-mode hover target to the measured signal edge without toolbar-step rounding', () => {
    useConnectionStore.setState({ mode: 'USB' });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 5000 });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    const target = resolvePanTuneTarget(binHz(152));

    expect(target.snappedToSignal).toBe(true);
    expect(target.fromLive).toBe(true);
    expect(target.tuneHz).toBe(Math.round(binHz(140)));
    expect(target.tuneHz % 5000).not.toBe(0);
  });

  it('click snap posts the same exact signal target that hover resolves', async () => {
    useConnectionStore.setState({ mode: 'USB', ctunEnabled: true });
    useSignalEnhanceStore.setState({ snapEnabled: true, snapRadiusHz: 3000, snapMinSnrDb: 5 });
    useToolbarFavoritesStore.setState({ stepHz: 5000 });
    const spec = voiceBlock();
    for (let k = 0; k < 5; k++) pushSnapFrame(spec, k + 1);

    const { container, unmount } = render(createElement(GestureProbe, { touchMode: 'normal' }));
    const canvas = container.querySelector('canvas') as HTMLCanvasElement;
    const clickX = (152 / SNAP_WIDTH) * 200;

    await act(async () => {
      pointer(canvas, 'pointerdown', { pointerId: 1, clientX: clickX, pointerType: 'mouse' });
      pointer(canvas, 'pointerup', { pointerId: 1, clientX: clickX, pointerType: 'mouse' });
      await flush();
    });

    expect(setVfoMock).toHaveBeenCalledWith(Math.round(binHz(140)), undefined);

    unmount();
  });

  it('lets focused VFO A tune from the RX2 stitched surface geometry in CTUN', async () => {
    useConnectionStore.setState({
      mode: 'USB',
      ctunEnabled: true,
      vfoHz: 14_200_000,
      vfoBHz: 7_200_000,
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
});
