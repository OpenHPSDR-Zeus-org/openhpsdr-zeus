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
// Zeus is an independent reimplementation in .NET — not a fork. Its TX
// behaviour and the two-tone IMD readout were informed by studying the Thetis
// project (https://github.com/ramdor/Thetis) — display.cs two_tone_readings,
// MW0LGE — the authoritative reference implementation in the OpenHPSDR
// ecosystem.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState } from 'react';

import { useTxStore } from '../state/tx-store';
import { useDisplayStore } from '../state/display-store';
import { computeImd, type ImdReadout, type ImdResult } from '../util/imd-measure';

// Two-tone IMD readout overlay. Pops up while the two-tone test is engaged and
// closes when it's switched off (render is gated on `twoToneOn`). It peak-finds
// the panadapter spectrum and reports IMD3 / IMD5 suppression in dBc plus the
// output intercept points — the objective meter for judging PureSignal, instead
// of eyeballing the trace. Source-agnostic: it measures whatever the panadapter
// shows, so view the post-PA feedback / a monitor RX to read PA IMD.

// EMA factor for the readout — the per-frame peak picks jitter a few tenths of a
// dB, so smooth lightly for a readable display without lagging real changes.
const EMA_ALPHA = 0.25;
// Recompute cadence. The spectrum ticks ~25 Hz; 10 Hz is plenty for a numeric
// readout and keeps the work off the render path.
const TICK_MS = 100;

function blend(prev: ImdReadout, next: ImdReadout, a: number): ImdReadout {
  const m = (p: number, n: number) => a * n + (1 - a) * p;
  return {
    ok: true,
    f0LowerDbm: m(prev.f0LowerDbm, next.f0LowerDbm),
    f0UpperDbm: m(prev.f0UpperDbm, next.f0UpperDbm),
    f0LowerHz: m(prev.f0LowerHz, next.f0LowerHz),
    f0UpperHz: m(prev.f0UpperHz, next.f0UpperHz),
    toneSpacingHz: m(prev.toneSpacingHz, next.toneSpacingHz),
    imd3: {
      lowerDbm: m(prev.imd3.lowerDbm, next.imd3.lowerDbm),
      upperDbm: m(prev.imd3.upperDbm, next.imd3.upperDbm),
      dbc: m(prev.imd3.dbc, next.imd3.dbc),
      lowerHz: m(prev.imd3.lowerHz, next.imd3.lowerHz),
      upperHz: m(prev.imd3.upperHz, next.imd3.upperHz),
    },
    imd5: {
      lowerDbm: m(prev.imd5.lowerDbm, next.imd5.lowerDbm),
      upperDbm: m(prev.imd5.upperDbm, next.imd5.upperDbm),
      dbc: m(prev.imd5.dbc, next.imd5.dbc),
      lowerHz: m(prev.imd5.lowerHz, next.imd5.lowerHz),
      upperHz: m(prev.imd5.upperHz, next.imd5.upperHz),
    },
    oip3: m(prev.oip3, next.oip3),
    oip5: m(prev.oip5, next.oip5),
  };
}

export function ImdReadings() {
  const twoToneOn = useTxStore((s) => s.twoToneOn);
  const [result, setResult] = useState<ImdResult | null>(null);
  const emaRef = useRef<ImdReadout | null>(null);

  useEffect(() => {
    if (!twoToneOn) {
      emaRef.current = null;
      setResult(null);
      return;
    }
    const id = window.setInterval(() => {
      const s = useDisplayStore.getState();
      if (!s.panValid || !s.panDb || s.panDb.length < 16 || s.hzPerPixel <= 0) {
        emaRef.current = null;
        setResult({ ok: false, reason: 'waiting for spectrum' });
        return;
      }
      const r = computeImd({
        db: s.panDb,
        width: s.panDb.length,
        centerHz: Number(s.centerHz),
        hzPerPixel: s.hzPerPixel,
      });
      if (r.ok) {
        const smoothed = emaRef.current ? blend(emaRef.current, r, EMA_ALPHA) : r;
        emaRef.current = smoothed;
        setResult(smoothed);
      } else {
        emaRef.current = null;
        setResult(r);
      }
    }, TICK_MS);
    return () => window.clearInterval(id);
  }, [twoToneOn]);

  if (!twoToneOn || result === null) return null;

  return (
    <div
      aria-label="Two-tone IMD measurements"
      className="pointer-events-none absolute z-[6]"
      style={{
        top: 10,
        left: 10,
        minWidth: 248,
        padding: '14px 18px',
        borderRadius: 10,
        background: 'rgba(10, 13, 17, 0.82)',
        border: '1px solid rgba(255, 160, 40, 0.45)',
        boxShadow: '0 3px 14px rgba(0,0,0,0.5)',
        font: '17px/1.5 "Archivo Narrow", system-ui, sans-serif',
        color: 'var(--fg-1)',
        letterSpacing: '0.02em',
        userSelect: 'none',
      }}
    >
      <div
        style={{
          fontWeight: 700,
          fontSize: 14,
          letterSpacing: '0.12em',
          color: 'var(--fg-2)',
          marginBottom: 8,
        }}
      >
        2-TONE IMD
      </div>
      {result.ok ? <Readout r={result} /> : <Miss reason={result.reason} />}
    </div>
  );
}

function Readout({ r }: { r: ImdReadout }) {
  const amber = 'var(--power)';
  // dbc is positive (product below the fundamental); show it as a negative
  // suppression figure, the convention operators read on a two-tone.
  const row = (label: string, value: string) => (
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: 24, marginBottom: 2 }}>
      <span style={{ color: 'var(--fg-2)' }}>{label}</span>
      <span style={{ color: amber, fontVariantNumeric: 'tabular-nums', fontWeight: 600 }}>{value}</span>
    </div>
  );
  return (
    <>
      {row('IMD3', `${(-r.imd3.dbc).toFixed(1)} dBc`)}
      {row('IMD5', `${(-r.imd5.dbc).toFixed(1)} dBc`)}
      {row('OIP3', `${r.oip3.toFixed(0)} dBm`)}
      <div
        style={{
          marginTop: 7,
          paddingTop: 7,
          borderTop: '1px solid rgba(255,255,255,0.08)',
          color: 'var(--fg-3)',
          fontSize: 13,
          display: 'flex',
          justifyContent: 'space-between',
          gap: 24,
        }}
      >
        <span>Δf {Math.round(r.toneSpacingHz)} Hz</span>
        <span>f0 {r.f0LowerDbm.toFixed(0)}/{r.f0UpperDbm.toFixed(0)} dBm</span>
      </div>
    </>
  );
}

function Miss({ reason }: { reason: string }) {
  return (
    <div style={{ color: 'var(--fg-3)', fontSize: 13, maxWidth: 220 }}>
      Peaks not found — {reason}.
      <br />
      Ensure both tones + their IMD products are in view.
    </div>
  );
}
