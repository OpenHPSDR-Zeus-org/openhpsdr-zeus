// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// TX EQ Analyzer — replaces the waterfall during MOX/TUN with a live,
// parametric-EQ-style view of the transmitted audio. The spectrum trace is the
// browser mic AnalyserNode tap (the audio actually going to the radio); the EQ
// response curve is the real on-air shaping (TX bandpass + CFC makeup bands);
// the telemetry strip reads the WDSP per-stage levels streamed at 10 Hz over
// the TxMetersV2 WS frame. Pure maths lives in audio/tx-eq-analyzer-model.ts.

import { useEffect, useRef } from 'react';
import { getMicSpectrum, getMicSpectrumInfo } from '../audio/mic-uplink-session';
import { useTxStore } from '../state/tx-store';
import { useConnectionStore } from '../state/connection-store';
import {
  DB_MIN,
  F_MAX,
  F_MIN,
  binsToColumns,
  buildEqCurve,
  dbToY,
  formatStageDb,
  freqToX,
  gainToY,
  type EqBand,
} from '../audio/tx-eq-analyzer-model';

// Frequency grid lines (Hz) and their short labels.
const GRID_FREQS: readonly { hz: number; label: string }[] = [
  { hz: 50, label: '50' },
  { hz: 100, label: '100' },
  { hz: 300, label: '300' },
  { hz: 1000, label: '1k' },
  { hz: 2000, label: '2k' },
  { hz: 3000, label: '3k' },
  { hz: 5000, label: '5k' },
];
const GRID_DBS = [-20, -40, -60, -80] as const;
// Peak-hold decay (dB per second) so transients linger briefly then fall.
const PEAK_DECAY_DB_PER_S = 36;

interface Palette {
  bg: string;
  grid: string;
  gridStrong: string;
  text: string;
  muted: string;
  tx: string;
  txSoft: string;
  curve: string;
  curveSoft: string;
  power: string;
}

function readPalette(el: HTMLElement): Palette {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string) => {
    const raw = cs.getPropertyValue(name).trim();
    return raw || fallback;
  };
  return {
    bg: v('--bg-inset', '#060608'),
    grid: v('--spec-grid', 'rgba(255,255,255,0.08)'),
    gridStrong: v('--line-strong', '#2c2c32'),
    text: v('--fg-1', '#cccccc'),
    muted: v('--fg-3', '#5a5a60'),
    tx: v('--tx', '#ff4a59'),
    txSoft: v('--tx-soft', 'rgba(255,74,89,0.18)'),
    curve: v('--accent-bright', '#4ea6ff'),
    curveSoft: v('--accent-soft', 'rgba(46,142,255,0.14)'),
    power: v('--power', '#ffb13c'),
  };
}

interface StageChip {
  key: string;
  label: string;
  pk: number;
  gr?: number;
}

export interface TxEqAnalyzerProps {
  transparent?: boolean;
}

export function TxEqAnalyzer({ transparent = false }: TxEqAnalyzerProps) {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  // EQ curve inputs — read live in the draw loop via refs to avoid re-mounting
  // the rAF loop on every parameter tweak.
  const cfcConfig = useTxStore((s) => s.cfcConfig);
  const txFilterLowHz = useConnectionStore((s) => s.txFilterLowHz);
  const txFilterHighHz = useConnectionStore((s) => s.txFilterHighHz);
  const tunOn = useTxStore((s) => s.tunOn);

  const eqInputRef = useRef({
    filterLowHz: txFilterLowHz,
    filterHighHz: txFilterHighHz,
    bands: cfcConfig.bands as readonly EqBand[],
    bandsEnabled: cfcConfig.enabled,
  });
  eqInputRef.current = {
    filterLowHz: txFilterLowHz,
    filterHighHz: txFilterHighHz,
    bands: cfcConfig.bands,
    bandsEnabled: cfcConfig.enabled,
  };

  useEffect(() => {
    const wrap = wrapRef.current;
    const canvas = canvasRef.current;
    if (!wrap || !canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let raf = 0;
    let cssW = 0;
    let cssH = 0;
    let dpr = 1;
    let palette = readPalette(wrap);

    let specBins: Float32Array | null = null;
    let cols: Float32Array | null = null;
    let peak: Float32Array | null = null;
    let curve: Float32Array | null = null;
    let specInfo = getMicSpectrumInfo();
    let lastT = performance.now();

    const resize = () => {
      const r = wrap.getBoundingClientRect();
      cssW = Math.max(1, Math.round(r.width));
      cssH = Math.max(1, Math.round(r.height));
      dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
      canvas.width = Math.round(cssW * dpr);
      canvas.height = Math.round(cssH * dpr);
      canvas.style.width = `${cssW}px`;
      canvas.style.height = `${cssH}px`;
      palette = readPalette(wrap);
      cols = new Float32Array(cssW);
      peak = new Float32Array(cssW).fill(DB_MIN);
      curve = new Float32Array(cssW);
    };
    resize();

    const ro = new ResizeObserver(resize);
    ro.observe(wrap);

    const draw = () => {
      raf = requestAnimationFrame(draw);
      const now = performance.now();
      const dt = Math.min(0.1, (now - lastT) / 1000);
      lastT = now;
      if (!cols || !peak || !curve) return;

      ctx.save();
      ctx.scale(dpr, dpr);
      const W = cssW;
      const H = cssH;
      // Reserve the top strip for telemetry (HTML overlay), draw plot below.
      const plotTop = 28;
      const plotH = Math.max(1, H - plotTop - 16);

      // Background.
      ctx.clearRect(0, 0, W, H);
      if (!transparent) {
        ctx.fillStyle = palette.bg;
        ctx.fillRect(0, 0, W, H);
      }

      // dB grid lines + labels.
      ctx.strokeStyle = palette.grid;
      ctx.fillStyle = palette.muted;
      ctx.lineWidth = 1;
      ctx.font = '10px Archivo Narrow, system-ui, sans-serif';
      ctx.textBaseline = 'middle';
      for (const db of GRID_DBS) {
        const y = dbToY(db, plotTop, plotH);
        ctx.beginPath();
        ctx.moveTo(0, y + 0.5);
        ctx.lineTo(W, y + 0.5);
        ctx.stroke();
        ctx.fillText(`${db}`, 3, y - 6);
      }
      // Frequency grid lines + labels.
      ctx.textBaseline = 'alphabetic';
      for (const g of GRID_FREQS) {
        if (g.hz < F_MIN || g.hz > F_MAX) continue;
        const x = freqToX(g.hz, 0, W);
        ctx.beginPath();
        ctx.moveTo(x + 0.5, plotTop);
        ctx.lineTo(x + 0.5, plotTop + plotH);
        ctx.stroke();
        ctx.fillText(g.label, x + 3, plotTop + plotH + 12);
      }

      // Live spectrum.
      const live = specInfo
        ? (() => {
            if (!specBins || specBins.length !== specInfo!.binCount) {
              specBins = new Float32Array(specInfo!.binCount);
            }
            return getMicSpectrum(specBins);
          })()
        : false;
      if (!live) {
        // tap may have come up since mount (mic granted after keying)
        specInfo = getMicSpectrumInfo();
      }

      if (live && specBins) {
        binsToColumns(specBins, W, specInfo!.sampleRate, specInfo!.fftSize, cols);
        // Decay + update peak-hold.
        const decay = PEAK_DECAY_DB_PER_S * dt;
        for (let x = 0; x < W; x += 1) {
          const c = cols[x] ?? DB_MIN;
          const p = (peak[x] ?? DB_MIN) - decay;
          peak[x] = c > p ? c : p;
        }

        // Filled spectrum.
        ctx.beginPath();
        ctx.moveTo(0, plotTop + plotH);
        for (let x = 0; x < W; x += 1) ctx.lineTo(x, dbToY(cols[x] ?? DB_MIN, plotTop, plotH));
        ctx.lineTo(W, plotTop + plotH);
        ctx.closePath();
        const grad = ctx.createLinearGradient(0, plotTop, 0, plotTop + plotH);
        grad.addColorStop(0, palette.tx);
        grad.addColorStop(1, palette.txSoft);
        ctx.globalAlpha = 0.55;
        ctx.fillStyle = grad;
        ctx.fill();
        ctx.globalAlpha = 1;

        // Spectrum edge.
        ctx.beginPath();
        for (let x = 0; x < W; x += 1) {
          const y = dbToY(cols[x] ?? DB_MIN, plotTop, plotH);
          if (x === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = palette.tx;
        ctx.lineWidth = 1.5;
        ctx.stroke();

        // Peak-hold trace.
        ctx.beginPath();
        for (let x = 0; x < W; x += 1) {
          const y = dbToY(peak[x] ?? DB_MIN, plotTop, plotH);
          if (x === 0) ctx.moveTo(x, y);
          else ctx.lineTo(x, y);
        }
        ctx.strokeStyle = palette.power;
        ctx.globalAlpha = 0.7;
        ctx.lineWidth = 1;
        ctx.stroke();
        ctx.globalAlpha = 1;
      } else {
        ctx.fillStyle = palette.muted;
        ctx.font = '12px Archivo Narrow, system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('Live audio analysis available in browser (mic) mode', W / 2, plotTop + plotH / 2);
        ctx.textAlign = 'left';
      }

      // EQ response curve (always drawn — the on-air shaping).
      buildEqCurve(eqInputRef.current, W, curve);
      ctx.beginPath();
      for (let x = 0; x < W; x += 1) {
        const y = gainToY(curve[x] ?? 0, plotTop, plotH);
        if (x === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = palette.curve;
      ctx.lineWidth = 2;
      ctx.stroke();

      // Unity (0 dB gain) reference line.
      const unityY = gainToY(0, plotTop, plotH);
      ctx.beginPath();
      ctx.setLineDash([3, 4]);
      ctx.moveTo(0, unityY + 0.5);
      ctx.lineTo(W, unityY + 0.5);
      ctx.strokeStyle = palette.curveSoft;
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.setLineDash([]);

      // Band nodes.
      const inp = eqInputRef.current;
      if (inp.bandsEnabled) {
        for (const band of inp.bands) {
          if (!(band.freqHz >= F_MIN && band.freqHz <= F_MAX)) continue;
          const x = freqToX(band.freqHz, 0, W);
          const y = gainToY(band.postGainDb, plotTop, plotH);
          ctx.beginPath();
          ctx.arc(x, y, band.postGainDb !== 0 ? 4 : 2.5, 0, Math.PI * 2);
          ctx.fillStyle = band.postGainDb !== 0 ? palette.curve : palette.muted;
          ctx.fill();
        }
      }

      ctx.restore();
    };
    raf = requestAnimationFrame(draw);

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, [transparent]);

  return (
    <div
      ref={wrapRef}
      data-tx-eq-analyzer
      style={{ position: 'relative', width: '100%', height: '100%', minWidth: 0, minHeight: 0 }}
    >
      <canvas ref={canvasRef} style={{ display: 'block', width: '100%', height: '100%' }} />
      <TxEqTelemetryStrip tune={tunOn} />
    </div>
  );
}

function TxEqTelemetryStrip({ tune }: { tune: boolean }) {
  const fwdWatts = useTxStore((s) => s.fwdWatts);
  const swr = useTxStore((s) => s.swr);
  const micPk = useTxStore((s) => s.wdspMicPk);
  const eqPk = useTxStore((s) => s.eqPk);
  const lvlrPk = useTxStore((s) => s.lvlrPk);
  const lvlrGr = useTxStore((s) => s.lvlrGr);
  const cfcPk = useTxStore((s) => s.cfcPk);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const compPk = useTxStore((s) => s.compPk);
  const alcPk = useTxStore((s) => s.alcPk);
  const alcGr = useTxStore((s) => s.alcGr);
  const outPk = useTxStore((s) => s.outPk);

  const stages: StageChip[] = [
    { key: 'mic', label: 'MIC', pk: micPk },
    { key: 'eq', label: 'EQ', pk: eqPk },
    { key: 'lvlr', label: 'LVLR', pk: lvlrPk, gr: lvlrGr },
    { key: 'cfc', label: 'CFC', pk: cfcPk, gr: cfcGr },
    { key: 'comp', label: 'COMP', pk: compPk },
    { key: 'alc', label: 'ALC', pk: alcPk, gr: alcGr },
    { key: 'out', label: 'OUT', pk: outPk },
  ];

  const swrText = Number.isFinite(swr) && swr > 0 ? `${swr.toFixed(2)}:1` : '—';

  return (
    <div
      style={{
        position: 'absolute',
        top: 0,
        left: 0,
        right: 0,
        height: 26,
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        padding: '0 8px',
        font: '11px Archivo Narrow, system-ui, sans-serif',
        color: 'var(--fg-2)',
        pointerEvents: 'none',
        userSelect: 'none',
        overflow: 'hidden',
        whiteSpace: 'nowrap',
      }}
    >
      <span
        style={{
          color: '#fff',
          background: 'var(--tx)',
          borderRadius: 3,
          padding: '1px 6px',
          fontWeight: 700,
          letterSpacing: '0.04em',
        }}
      >
        {tune ? 'TUN' : 'TX'} EQ
      </span>
      <span style={{ color: 'var(--power)' }}>{Number.isFinite(fwdWatts) ? `${fwdWatts.toFixed(0)} W` : '— W'}</span>
      <span>SWR {swrText}</span>
      <span style={{ flex: '0 0 1px', alignSelf: 'stretch', background: 'var(--line-strong)' }} />
      {stages.map((s) => (
        <span key={s.key} style={{ display: 'inline-flex', alignItems: 'baseline', gap: 3 }}>
          <span style={{ color: 'var(--fg-3)' }}>{s.label}</span>
          <span style={{ color: 'var(--fg-1)', fontVariantNumeric: 'tabular-nums' }}>{formatStageDb(s.pk)}</span>
          {s.gr !== undefined && s.gr > 0.1 && (
            <span style={{ color: 'var(--tx)', fontVariantNumeric: 'tabular-nums' }}>−{s.gr.toFixed(1)}</span>
          )}
        </span>
      ))}
    </div>
  );
}
