// SPDX-License-Identifier: GPL-2.0-or-later
//
// Verifies the active-consumer registry that gates decodeDisplayFrame in
// ws-client.ts. The contract: hasActiveFrameConsumers() reports whether at
// least one panadapter / waterfall / filter mini-pan is mounted; ws-client
// short-circuits the per-frame decode when it returns false.

import { afterEach, describe, expect, it } from 'vitest';
import {
  _resetFrameConsumerCount,
  hasActiveFrameConsumers,
  registerFrameConsumer,
  sanitizeDisplayBins,
  useDisplayStore,
} from './display-store';
import type { DecodedFrame } from '../realtime/frame';

afterEach(() => {
  _resetFrameConsumerCount();
  useDisplayStore.setState({
    connected: false,
    width: 0,
    centerHz: 0n,
    hzPerPixel: 0,
    panDb: null,
    wfDb: null,
    panValid: false,
    wfValid: false,
    lastSeq: 0,
  });
});

describe('frame consumer registry', () => {
  it('reports no consumers initially', () => {
    expect(hasActiveFrameConsumers()).toBe(false);
  });

  it('flips to true while a consumer is registered', () => {
    const release = registerFrameConsumer();
    expect(hasActiveFrameConsumers()).toBe(true);
    release();
    expect(hasActiveFrameConsumers()).toBe(false);
  });

  it('stays true while at least one consumer remains', () => {
    const a = registerFrameConsumer();
    const b = registerFrameConsumer();
    expect(hasActiveFrameConsumers()).toBe(true);
    a();
    expect(hasActiveFrameConsumers()).toBe(true);
    b();
    expect(hasActiveFrameConsumers()).toBe(false);
  });

  it('release is idempotent', () => {
    const release = registerFrameConsumer();
    release();
    release();
    expect(hasActiveFrameConsumers()).toBe(false);
    // A second consumer must still flip the flag back on.
    const next = registerFrameConsumer();
    expect(hasActiveFrameConsumers()).toBe(true);
    next();
  });

  it('count never goes negative under bad release ordering', () => {
    const a = registerFrameConsumer();
    a();
    a();
    expect(hasActiveFrameConsumers()).toBe(false);
    const b = registerFrameConsumer();
    expect(hasActiveFrameConsumers()).toBe(true);
    b();
    expect(hasActiveFrameConsumers()).toBe(false);
  });
});

describe('display frame bin sanitizer', () => {
  it('returns the original array when every bin is finite', () => {
    const bins = new Float32Array([-120, -80, -42.5]);

    expect(sanitizeDisplayBins(bins)).toBe(bins);
  });

  it('copies and floors non-finite bins without changing finite dB values', () => {
    const bins = new Float32Array([-120, Number.NaN, -42.5, Infinity, -Infinity]);

    const sanitized = sanitizeDisplayBins(bins);

    expect(sanitized).not.toBe(bins);
    expect(Array.from(sanitized)).toEqual([-120, -200, -42.5, -200, -200]);
    expect(Number.isNaN(bins[1])).toBe(true);
  });

  it('pushFrame stores sanitized bins before publishing state', () => {
    const panDb = new Float32Array([-88, Number.NaN, -76, Infinity]);
    const wfDb = new Float32Array([-95, -92, -90, -89]);
    const frame: DecodedFrame = {
      msgType: 0x01,
      headerFlags: 0,
      seq: 99,
      tsUnixMs: 1_700_000_000_000,
      rxId: 0,
      bodyFlags: 0x03,
      panValid: true,
      wfValid: true,
      width: 4,
      centerHz: 14_074_000n,
      hzPerPixel: 46.875,
      panDb,
      wfDb,
    };

    useDisplayStore.getState().pushFrame(frame);

    const state = useDisplayStore.getState();
    expect(state.lastSeq).toBe(99);
    expect(state.panDb).not.toBe(panDb);
    expect(Array.from(state.panDb ?? [])).toEqual([-88, -200, -76, -200]);
    expect(state.wfDb).toBe(wfDb);
  });

  it('marks a valid-bit payload invalid when its bin count does not match frame width', () => {
    const panDb = new Float32Array([-88, -82, -76]);
    const wfDb = new Float32Array([-95, -92, -90, -89]);
    const frame: DecodedFrame = {
      msgType: 0x01,
      headerFlags: 0,
      seq: 100,
      tsUnixMs: 1_700_000_000_001,
      rxId: 0,
      bodyFlags: 0x03,
      panValid: true,
      wfValid: true,
      width: 4,
      centerHz: 14_074_000n,
      hzPerPixel: 46.875,
      panDb,
      wfDb,
    };

    useDisplayStore.getState().pushFrame(frame);

    const state = useDisplayStore.getState();
    expect(state.lastSeq).toBe(100);
    expect(state.width).toBe(4);
    expect(state.hzPerPixel).toBe(46.875);
    expect(state.panValid).toBe(false);
    expect(state.panDb).toBeNull();
    expect(state.wfValid).toBe(true);
    expect(state.wfDb).toBe(wfDb);
  });

  it('fails closed on unusable frame geometry', () => {
    const panDb = new Float32Array([-88, -82, -76, -74]);
    const wfDb = new Float32Array([-95, -92, -90, -89]);

    for (const [i, bad] of [
      { width: 0, hzPerPixel: 46.875 },
      { width: 4.5, hzPerPixel: 46.875 },
      { width: 4, hzPerPixel: Number.NaN },
      { width: 4, hzPerPixel: Infinity },
      { width: 4, hzPerPixel: 0 },
    ].entries()) {
      const frame: DecodedFrame = {
        msgType: 0x01,
        headerFlags: 0,
        seq: 200 + i,
        tsUnixMs: 1_700_000_000_002,
        rxId: 0,
        bodyFlags: 0x03,
        panValid: true,
        wfValid: true,
        centerHz: 14_074_000n,
        panDb,
        wfDb,
        ...bad,
      };

      useDisplayStore.getState().pushFrame(frame);

      const state = useDisplayStore.getState();
      expect(state.width).toBe(0);
      expect(state.hzPerPixel).toBe(0);
      expect(state.panValid).toBe(false);
      expect(state.wfValid).toBe(false);
      expect(state.panDb).toBeNull();
      expect(state.wfDb).toBeNull();
    }
  });
});
