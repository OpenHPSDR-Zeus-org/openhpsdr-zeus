// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Shared meter ballistics hook. Every Zeus meter widget (BigArc, VuColumn,
// PullDownArc, HBarMeter, TxStageMeters rows, MicMeter) reads through this
// hook so they all get the same physics by default: moving-average prefilter
// → asymmetric attack/decay RC → peak-hold ghost, driven by a per-widget
// rAF loop publishing at ~30 Hz.
//
// Why per-widget rAF and not a central ticker: locality. Each widget pauses
// cleanly when the tab is hidden (`document.hidden` gate), and there's no
// reference-counting bookkeeping needed for meters that aren't currently
// mounted. The cost of N parallel rAF loops for the 6–12 meters typically
// visible is negligible (one Math.exp + a handful of arithmetic ops per
// tick per widget).
//
// Sampling: the loop calls `getValue()` once per tick, which should read
// from a Zustand store via `useStore.getState()` (no React subscriptions).
// We accept the getter via a ref so the caller can pass an inline closure
// without restarting the loop every render.

import { useEffect, useRef, useState, type RefObject } from 'react';
import { useTxStore } from '../../state/tx-store';
import { useRxMetersStore } from '../../state/rx-meters-store';
import { MeterReadingId } from './meterCatalog';
import {
  METER_BALLISTICS_DEFAULTS,
  ballisticsStep,
  isSilentSample,
  makeAverager,
  peakHoldStep,
} from './ballistics';
import type { Averager } from './ballistics';

export interface AxisSpan {
  min: number;
  max: number;
}

export interface BallisticReading {
  /** Smoothed value in raw units (dBFS, W, ratio, dB). NaN until the
   *  first finite sample lands; -∞ / sentinel passes through. */
  value: number;
  /** Peak-hold value in raw units. Same units as `value`. Returns NaN
   *  while no live sample has been seen and while the live signal is
   *  in the silent-sentinel range. */
  peak: number;
}

/** Publish at most every PUBLISH_INTERVAL_MS so high-refresh displays
 *  (macOS ProMotion runs rAF at 120 Hz) don't reconcile React 4× per
 *  render. 30 Hz gives smooth analog motion without driving the widget
 *  subtree off the system frame rate. */
const PUBLISH_INTERVAL_MS = 33;

/** Only publish when value or peak crosses this fraction of the axis
 *  span — avoids fractional updates that wouldn't move a pixel. */
const PUBLISH_DELTA_FRAC = 0.001;

interface InternalState {
  smoothed: number;
  peak: number;
  /** performance.now() of the last loop tick. */
  lastTickMs: number;
}

function freshState(): InternalState {
  return { smoothed: NaN, peak: NaN, lastTickMs: 0 };
}

/**
 * Generic ballistic reading hook. Pass a `getValue` that pulls the latest
 * raw reading from whichever store(s) you care about — typically a closure
 * around `useTxStore.getState().someField`.
 *
 * `span` is the meter's display axis. It's used to size the peak-decay
 * rate (5 % of span per second) and the publish-threshold (0.001 × span)
 * so widgets with different units feel the same.
 */
export function useBallisticReading(
  getValue: () => number,
  span: AxisSpan,
  /** Optional ref to the widget's root element. When provided, an
   *  IntersectionObserver pauses the rAF loop while the element is
   *  off-screen — same pattern AnalogMeterPanel / Panadapter / Waterfall
   *  use. If omitted, only the document.hidden tab-visibility gate
   *  applies. */
  targetRef?: RefObject<Element | null>,
): BallisticReading {
  // Stash the getter in a ref so the rAF loop always reads the latest
  // closure (including any captured props/state) without restarting on
  // every parent render.
  const getterRef = useRef(getValue);
  getterRef.current = getValue;

  const stateRef = useRef<InternalState>(freshState());
  const avgRef = useRef<Averager>(makeAverager(METER_BALLISTICS_DEFAULTS.avgSamples));

  const [published, setPublished] = useState<BallisticReading>({
    value: NaN,
    peak: NaN,
  });
  const lastPublishedRef = useRef<BallisticReading>({ value: NaN, peak: NaN });
  const lastPublishMsRef = useRef(0);

  useEffect(() => {
    let raf = 0;
    let pageVisible =
      typeof document !== 'undefined' ? !document.hidden : true;
    // When no targetRef is supplied we have no way to ask "are you on
    // screen?" — default to true so the loop runs (callers can pass a
    // ref to opt into IntersectionObserver gating).
    let inViewport = true;
    const isActive = () => inViewport && pageVisible;

    const loop = (now: number) => {
      raf = 0;
      const st = stateRef.current;

      // dt seed — first tick after mount / wake skips the elapsed gap so
      // the smoother doesn't get yanked.
      const dt =
        st.lastTickMs === 0
          ? 0
          : Math.min(0.5, Math.max(0, (now - st.lastTickMs) / 1000));
      st.lastTickMs = now;

      const raw = getterRef.current();

      // Sentinel passthrough — reset state so the next live sample
      // doesn't lerp out of -∞.
      if (isSilentSample(raw)) {
        if (
          !Number.isNaN(st.smoothed) ||
          !Number.isNaN(st.peak)
        ) {
          st.smoothed = raw;
          st.peak = NaN;
          avgRef.current.reset();
          publish({ value: raw, peak: NaN }, now, true);
        }
        if (isActive()) raf = requestAnimationFrame(loop);
        return;
      }

      // First live sample seeds the smoother + peak so we don't ramp from 0.
      if (Number.isNaN(st.smoothed)) {
        st.smoothed = raw;
        st.peak = raw;
        avgRef.current.reset();
        avgRef.current.push(raw);
        publish({ value: raw, peak: raw }, now, true);
        if (isActive()) raf = requestAnimationFrame(loop);
        return;
      }

      const averaged = avgRef.current.push(raw);
      st.smoothed = ballisticsStep(
        st.smoothed,
        averaged,
        dt,
        METER_BALLISTICS_DEFAULTS.attackSec,
        METER_BALLISTICS_DEFAULTS.decaySec,
      );
      st.peak = peakHoldStep(
        Number.isNaN(st.peak) ? st.smoothed : st.peak,
        st.smoothed,
        dt,
        span.min,
        span.max,
        METER_BALLISTICS_DEFAULTS.peakDecayFracPerSec,
      );

      publish({ value: st.smoothed, peak: st.peak }, now, false);

      if (isActive()) raf = requestAnimationFrame(loop);
    };

    const publish = (
      next: BallisticReading,
      now: number,
      force: boolean,
    ) => {
      if (
        !force &&
        now - lastPublishMsRef.current < PUBLISH_INTERVAL_MS
      ) {
        return;
      }
      const last = lastPublishedRef.current;
      const spanSize = Math.max(1e-9, span.max - span.min);
      const threshold = PUBLISH_DELTA_FRAC * spanSize;
      const valueChanged =
        Number.isNaN(last.value) !== Number.isNaN(next.value) ||
        (Number.isFinite(next.value) &&
          Math.abs(next.value - last.value) > threshold);
      const peakChanged =
        Number.isNaN(last.peak) !== Number.isNaN(next.peak) ||
        (Number.isFinite(next.peak) &&
          Math.abs(next.peak - last.peak) > threshold);
      if (!force && !valueChanged && !peakChanged) return;
      lastPublishMsRef.current = now;
      lastPublishedRef.current = next;
      setPublished(next);
    };

    const start = () => {
      if (raf === 0 && isActive()) {
        // Reset dt anchor so a wake doesn't account for the dark period
        // as elapsed time and yank the ballistic.
        stateRef.current.lastTickMs = 0;
        raf = requestAnimationFrame(loop);
      }
    };

    const onVisibilityChange = () => {
      pageVisible = !document.hidden;
      if (isActive()) start();
    };

    // IntersectionObserver visibility gate — pause the rAF loop entirely
    // while the widget is scrolled off-screen, in a collapsed dockable,
    // or in a hidden tab panel. Same pattern AnalogMeterPanel and the
    // panadapter / waterfall use.
    let io: IntersectionObserver | null = null;
    const target = targetRef?.current ?? null;
    if (target && typeof IntersectionObserver !== 'undefined') {
      io = new IntersectionObserver(
        (entries) => {
          for (const e of entries) inViewport = e.isIntersecting;
          if (isActive()) start();
        },
        { threshold: 0 },
      );
      io.observe(target);
    }

    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', onVisibilityChange);
    }
    start();

    return () => {
      if (raf !== 0) cancelAnimationFrame(raf);
      if (io) io.disconnect();
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', onVisibilityChange);
      }
    };
    // Span min/max change should rebuild peak math but not reset the
    // smoother — peak is in raw units, so a new axis affects the decay
    // rate going forward.
  }, [span.min, span.max, targetRef]);

  return published;
}

// ── Per-MeterReadingId convenience wrapper ────────────────────────────────
//
// Mirrors useMeterReading.ts's switch, but reads via getState() so the
// rAF loop can sample without subscribing. Keep this in lockstep with
// useMeterReading.ts — any new field added to the catalog needs an entry
// here too.

function sampleByMeterId(id: MeterReadingId): number {
  const tx = useTxStore.getState();
  const rx = useRxMetersStore.getState();
  switch (id) {
    case MeterReadingId.RxSignalPk:
      return rx.signalPk;
    case MeterReadingId.RxSignalAv:
      return rx.signalAv;
    case MeterReadingId.RxAdcPk:
      return rx.adcPk;
    case MeterReadingId.RxAdcAv:
      return rx.adcAv;
    case MeterReadingId.RxAgcGain:
      return rx.agcGain;
    case MeterReadingId.RxAgcEnvPk:
      return rx.agcEnvPk;
    case MeterReadingId.RxAgcEnvAv:
      return rx.agcEnvAv;
    case MeterReadingId.TxFwdWatts:
      return tx.fwdWatts;
    case MeterReadingId.TxRefWatts:
      return tx.refWatts;
    case MeterReadingId.TxSwr:
      return tx.swr;
    case MeterReadingId.TxMicPk:
      return tx.wdspMicPk;
    case MeterReadingId.TxMicAv:
      return tx.micAv;
    case MeterReadingId.TxEqPk:
      return tx.eqPk;
    case MeterReadingId.TxEqAv:
      return tx.eqAv;
    case MeterReadingId.TxLvlrPk:
      return tx.lvlrPk;
    case MeterReadingId.TxLvlrAv:
      return tx.lvlrAv;
    case MeterReadingId.TxLvlrGr:
      return tx.lvlrGr;
    case MeterReadingId.TxCfcPk:
      return tx.cfcPk;
    case MeterReadingId.TxCfcAv:
      return tx.cfcAv;
    case MeterReadingId.TxCfcGr:
      return tx.cfcGr;
    case MeterReadingId.TxCompPk:
      return tx.compPk;
    case MeterReadingId.TxCompAv:
      return tx.compAv;
    case MeterReadingId.TxAlcPk:
      return tx.alcPk;
    case MeterReadingId.TxAlcAv:
      return tx.alcAv;
    case MeterReadingId.TxAlcGr:
      return tx.alcGr;
    case MeterReadingId.TxOutPk:
      return tx.outPk;
    case MeterReadingId.TxOutAv:
      return tx.outAv;
  }
}

/** Convenience: smooth a catalog reading by id. Equivalent to
 *  `useBallisticReading(() => sample(id), span, targetRef)`. */
export function useBallisticReadingById(
  id: MeterReadingId,
  span: AxisSpan,
  targetRef?: RefObject<Element | null>,
): BallisticReading {
  return useBallisticReading(() => sampleByMeterId(id), span, targetRef);
}
