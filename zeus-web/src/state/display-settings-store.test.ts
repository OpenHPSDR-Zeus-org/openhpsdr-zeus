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

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DEFAULT_TX_DISPLAY_AVG_TAU_MS,
  DEFAULT_TX_DISPLAY_CAL_OFFSET_DB,
  DEFAULT_TX_DISPLAY_FFT_SIZE,
  DEFAULT_TX_DISPLAY_WINDOW,
  DEFAULT_WF_SCROLL_SPEED,
  FIXED_DB_MAX,
  FIXED_DB_MIN,
  TX_DISPLAY_CAL_OFFSET_ABS_DB,
  TX_DISPLAY_AVG_TAU_MAX_MS,
  WATERFALL_SCROLL_SPEED_MAX,
  WATERFALL_SCROLL_SPEED_MIN,
  TX_FIXED_DB_MAX,
  TX_FIXED_DB_MIN,
  useDisplaySettingsStore,
} from './display-settings-store';

function resetStore() {
  useDisplaySettingsStore.setState({
    autoRange: false,
    dbMin: FIXED_DB_MIN,
    dbMax: FIXED_DB_MAX,
    txDbMin: TX_FIXED_DB_MIN,
    txDbMax: TX_FIXED_DB_MAX,
    wfDbMin: FIXED_DB_MIN,
    wfDbMax: FIXED_DB_MAX,
    wfTxDbMin: TX_FIXED_DB_MIN,
    wfTxDbMax: TX_FIXED_DB_MAX,
    waterfallScrollSpeed: DEFAULT_WF_SCROLL_SPEED,
  });
}

describe('display-settings-store', () => {
  beforeEach(resetStore);

  it('returns the fixed range by default', () => {
    const { dbMin, dbMax, autoRange } = useDisplaySettingsStore.getState();
    expect(autoRange).toBe(false);
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('ignores updateAutoRange while autoRange is off', () => {
    const arr = new Float32Array(64).fill(-90);
    useDisplaySettingsStore.getState().updateAutoRange(arr);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('snaps back to fixed range when turned off', () => {
    const s = useDisplaySettingsStore.getState();
    s.setAutoRange(true);
    useDisplaySettingsStore.setState({ dbMin: -80, dbMax: -40 });
    useDisplaySettingsStore.getState().setAutoRange(false);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('drifts dbMin/dbMax toward percentile target with smoothing', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);

    // Flat noise floor at -95, plus a top 5% of strong peaks at -50.
    const n = 200;
    const arr = new Float32Array(n);
    for (let i = 0; i < n; i++) arr[i] = i < n * 0.95 ? -95 : -50;

    for (let k = 0; k < 200; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(arr);
    }

    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    // After many iterations the smoothed range converges on
    // (p5 - AUTO_FLOOR_MARGIN, p95 + AUTO_CEIL_MARGIN). p5 = -95, p95 = -50,
    // so targets are ≈ -103 and ≈ -44. Both should have moved well away
    // from the fixed defaults (-120, -30) toward the data.
    expect(dbMin).toBeGreaterThan(-110);
    expect(dbMax).toBeLessThan(-40);
    expect(dbMin).toBeLessThan(dbMax);
  });

  it('enforces a minimum span when the signal is flat', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    const flat = new Float32Array(128).fill(-80);
    for (let k = 0; k < 400; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(flat);
    }
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMax - dbMin).toBeGreaterThanOrEqual(19.9);
  });

  it('handles an empty array without producing NaN', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    useDisplaySettingsStore.getState().updateAutoRange(new Float32Array(0));
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(Number.isFinite(dbMin)).toBe(true);
    expect(Number.isFinite(dbMax)).toBe(true);
  });

  it('keeps smooth waterfall scroll speed by default', () => {
    const { waterfallScrollSpeed } = useDisplaySettingsStore.getState();
    expect(waterfallScrollSpeed).toBe(DEFAULT_WF_SCROLL_SPEED);
  });

  it('updates waterfall scroll speed continuously within operator limits', () => {
    const s = useDisplaySettingsStore.getState();

    s.setWaterfallScrollSpeed(0.55);
    expect(useDisplaySettingsStore.getState().waterfallScrollSpeed).toBe(0.55);

    s.setWaterfallScrollSpeed(1.37);
    expect(useDisplaySettingsStore.getState().waterfallScrollSpeed).toBe(1.35);

    s.setWaterfallScrollSpeed(-5);
    expect(useDisplaySettingsStore.getState().waterfallScrollSpeed).toBe(WATERFALL_SCROLL_SPEED_MIN);

    s.setWaterfallScrollSpeed(99);
    expect(useDisplaySettingsStore.getState().waterfallScrollSpeed).toBe(WATERFALL_SCROLL_SPEED_MAX);
  });

  it('sets explicit RX/TX panadapter and waterfall dB windows', () => {
    const s = useDisplaySettingsStore.getState();

    s.setAutoRange(true);
    s.setDbRange(-132, -42);
    s.setTxDbRange(-76, 14);
    s.setWfDbRange(-138, -58);
    s.setWfTxDbRange(-82, 18);

    expect(useDisplaySettingsStore.getState()).toMatchObject({
      autoRange: false,
      dbMin: -132,
      dbMax: -42,
      txDbMin: -76,
      txDbMax: 14,
      wfDbMin: -138,
      wfDbMax: -58,
      wfTxDbMin: -82,
      wfTxDbMax: 18,
    });
  });

  it('resets all explicit dB windows to defaults', () => {
    const s = useDisplaySettingsStore.getState();
    s.setDbRange(-132, -42);
    s.setTxDbRange(-76, 14);
    s.setWfDbRange(-138, -58);
    s.setWfTxDbRange(-82, 18);

    s.resetDbRanges();

    expect(useDisplaySettingsStore.getState()).toMatchObject({
      autoRange: false,
      dbMin: FIXED_DB_MIN,
      dbMax: FIXED_DB_MAX,
      txDbMin: TX_FIXED_DB_MIN,
      txDbMax: TX_FIXED_DB_MAX,
      wfDbMin: FIXED_DB_MIN,
      wfDbMax: FIXED_DB_MAX,
      wfTxDbMin: TX_FIXED_DB_MIN,
      wfTxDbMax: TX_FIXED_DB_MAX,
    });
  });
});

describe('TX display analyzer params', () => {
  beforeEach(() => {
    // Stub fetch so the debounced server save is a no-op (and never leaks a
    // rejected promise / pending timer into later tests).
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({}) })));
    useDisplaySettingsStore.setState({
      txDisplayCalOffsetDb: DEFAULT_TX_DISPLAY_CAL_OFFSET_DB,
      txDisplayFftSize: DEFAULT_TX_DISPLAY_FFT_SIZE,
      txDisplayWindow: DEFAULT_TX_DISPLAY_WINDOW,
      txDisplayAvgTauMs: DEFAULT_TX_DISPLAY_AVG_TAU_MS,
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('updates a single param and leaves the rest untouched', () => {
    useDisplaySettingsStore.getState().setTxDisplayParams({ fftSize: 32768 });
    const s = useDisplaySettingsStore.getState();
    expect(s.txDisplayFftSize).toBe(32768);
    expect(s.txDisplayCalOffsetDb).toBe(DEFAULT_TX_DISPLAY_CAL_OFFSET_DB);
    expect(s.txDisplayWindow).toBe(DEFAULT_TX_DISPLAY_WINDOW);
    expect(s.txDisplayAvgTauMs).toBe(DEFAULT_TX_DISPLAY_AVG_TAU_MS);
  });

  it('clamps the cal offset to ±limit', () => {
    useDisplaySettingsStore.getState().setTxDisplayParams({ calOffsetDb: 9999 });
    expect(useDisplaySettingsStore.getState().txDisplayCalOffsetDb).toBe(TX_DISPLAY_CAL_OFFSET_ABS_DB);
    useDisplaySettingsStore.getState().setTxDisplayParams({ calOffsetDb: -9999 });
    expect(useDisplaySettingsStore.getState().txDisplayCalOffsetDb).toBe(-TX_DISPLAY_CAL_OFFSET_ABS_DB);
  });

  it('clamps smoothing tau to the allowed range', () => {
    useDisplaySettingsStore.getState().setTxDisplayParams({ avgTauMs: 99999 });
    expect(useDisplaySettingsStore.getState().txDisplayAvgTauMs).toBe(TX_DISPLAY_AVG_TAU_MAX_MS);
  });

  it('ignores a non-power-of-two FFT size', () => {
    useDisplaySettingsStore.getState().setTxDisplayParams({ fftSize: 12345 });
    expect(useDisplaySettingsStore.getState().txDisplayFftSize).toBe(DEFAULT_TX_DISPLAY_FFT_SIZE);
  });

  it('ignores an unknown window type', () => {
    useDisplaySettingsStore.getState().setTxDisplayParams({ window: 99 });
    expect(useDisplaySettingsStore.getState().txDisplayWindow).toBe(DEFAULT_TX_DISPLAY_WINDOW);
  });

  it('resets all params to defaults', () => {
    const s = useDisplaySettingsStore.getState();
    s.setTxDisplayParams({ calOffsetDb: -15, fftSize: 8192, window: 5, avgTauMs: 400 });
    s.resetTxDisplayParams();
    expect(useDisplaySettingsStore.getState()).toMatchObject({
      txDisplayCalOffsetDb: DEFAULT_TX_DISPLAY_CAL_OFFSET_DB,
      txDisplayFftSize: DEFAULT_TX_DISPLAY_FFT_SIZE,
      txDisplayWindow: DEFAULT_TX_DISPLAY_WINDOW,
      txDisplayAvgTauMs: DEFAULT_TX_DISPLAY_AVG_TAU_MS,
    });
  });
});

describe('TX auto-range', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({}) })));
    useDisplaySettingsStore.setState({
      txAutoRange: true,
      txDbMin: -80,
      txDbMax: 20,
      wfTxDbMin: -80,
      wfTxDbMax: 20,
    });
  });
  afterEach(() => vi.unstubAllGlobals());

  // Synthetic TX frame: a wide noise floor with a passband block ~12% of the
  // width — the shape the WDSP TX analyzer produces (signal occupies only a
  // fraction of the full-span display).
  function txFrame(floorDb: number, peakDb: number): Float32Array {
    const n = 1024;
    const px = new Float32Array(n).fill(floorDb);
    for (let i = 450; i < 574; i++) px[i] = peakDb;
    return px;
  }

  it('fits both TX windows to the signal instead of clamping at full-scale', () => {
    const px = txFrame(-50, -10);
    const s = useDisplaySettingsStore.getState();
    for (let k = 0; k < 80; k++) s.updateTxAutoRange(px);
    const r = useDisplaySettingsStore.getState();
    // Ceiling settles just above the -10 dB peak — far below the +20 that was
    // saturating the whole passband — so the signal is visible, not maxed out.
    expect(r.txDbMax).toBeGreaterThan(-10);
    expect(r.txDbMax).toBeLessThan(6);
    // Floor sits below the signal so the passband fills the window.
    expect(r.txDbMin).toBeLessThan(-10);
    // Panadapter and waterfall windows track together.
    expect(r.wfTxDbMax).toBeCloseTo(r.txDbMax, 0);
    expect(r.wfTxDbMin).toBeCloseTo(r.txDbMin, 0);
  });

  it('is a no-op while turned off', () => {
    useDisplaySettingsStore.setState({ txAutoRange: false });
    useDisplaySettingsStore.getState().updateTxAutoRange(txFrame(-50, -10));
    const r = useDisplaySettingsStore.getState();
    expect(r.txDbMin).toBe(-80);
    expect(r.txDbMax).toBe(20);
  });

  it('a manual TX window edit switches auto-range off', () => {
    useDisplaySettingsStore.getState().setTxDbRange(-76, 14);
    expect(useDisplaySettingsStore.getState().txAutoRange).toBe(false);
  });
});
