// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live CW-band spectrogram waterfall for the DeepCW decoder panel. Taps the
// same RX audio bus the decoder uses and paints a scrolling amber waterfall
// so the operator can SEE the Morse while reading the decode. Amber
// (--amber) is Zeus's sanctioned signal-strength visualization colour; the
// 400–1200 Hz region the model decodes is marked with a faint frame.
//
// Goertzel per frequency bin (cheap, single-tone detectors) over a short
// sliding window — no FFT dependency, comfortably under a 64-bin × 2048-sample
// budget at the ~46 Hz RX frame cadence.

import { useEffect, useRef } from 'react';
import { getAudioBus } from '../../audio/audio-bus';

const BAND_MIN_HZ = 250;
const BAND_MAX_HZ = 1450;
const DECODE_MIN_HZ = 400;
const DECODE_MAX_HZ = 1200;
const BINS = 64;
const WINDOW_SAMPLES = 2048; // sliding analysis window (source rate)
const SCOPE_HEIGHT = 104;

// Decode-band frame position as % of height (low freq at the bottom).
const bandTopPct = ((BAND_MAX_HZ - DECODE_MAX_HZ) / (BAND_MAX_HZ - BAND_MIN_HZ)) * 100;
const bandBotPct = ((BAND_MAX_HZ - DECODE_MIN_HZ) / (BAND_MAX_HZ - BAND_MIN_HZ)) * 100;

function binFreq(i: number): number {
  return BAND_MIN_HZ + (i / (BINS - 1)) * (BAND_MAX_HZ - BAND_MIN_HZ);
}

export function CwSpectrogram({ active }: { active: boolean }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !active) return;

    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) return;

    // Sliding analysis window + precomputed Hann + per-bin Goertzel coeffs.
    const win = new Float32Array(WINDOW_SAMPLES);
    let winFilled = 0;
    let sourceRate = 48000;
    const hann = new Float32Array(WINDOW_SAMPLES);
    for (let i = 0; i < WINDOW_SAMPLES; i += 1) {
      hann[i] = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / WINDOW_SAMPLES);
    }
    let coeffs = new Float32Array(BINS);
    const computeCoeffs = (rate: number) => {
      for (let b = 0; b < BINS; b += 1) {
        coeffs[b] = 2 * Math.cos((2 * Math.PI * binFreq(b)) / rate);
      }
    };
    computeCoeffs(sourceRate);

    let peak = 1e-4; // adaptive normaliser
    let pendingColumn: Float32Array | null = null;

    const onFrame = (frame: { samples: Float32Array; channels: number; sampleRateHz: number }) => {
      if (frame.sampleRateHz && frame.sampleRateHz !== sourceRate) {
        sourceRate = frame.sampleRateHz;
        computeCoeffs(sourceRate);
      }
      const ch = frame.channels;
      const src = frame.samples;
      const n = ch <= 1 ? src.length : Math.floor(src.length / ch);
      // Append (mono) into the sliding window.
      for (let i = 0; i < n; i += 1) {
        const s = ch <= 1 ? src[i]! : src[i * ch]!;
        if (winFilled < WINDOW_SAMPLES) {
          win[winFilled++] = s;
        } else {
          win.copyWithin(0, 1);
          win[WINDOW_SAMPLES - 1] = s;
        }
      }
      if (winFilled < WINDOW_SAMPLES) return;

      // One Goertzel pass per bin over the windowed buffer → magnitude column.
      const col = new Float32Array(BINS);
      let colMax = 0;
      for (let b = 0; b < BINS; b += 1) {
        const c = coeffs[b]!;
        let s0 = 0;
        let s1 = 0;
        let s2 = 0;
        for (let i = 0; i < WINDOW_SAMPLES; i += 1) {
          s0 = win[i]! * hann[i]! + c * s1 - s2;
          s2 = s1;
          s1 = s0;
        }
        const power = s1 * s1 + s2 * s2 - c * s1 * s2;
        const mag = power > 0 ? Math.sqrt(power) : 0;
        col[b] = mag;
        if (mag > colMax) colMax = mag;
      }
      if (colMax > peak) peak = colMax;
      pendingColumn = col;
    };

    const unsubscribe = getAudioBus().subscribe(onFrame);

    // Size the backing store to the element (DPR-aware) and clear.
    const fit = () => {
      const w = Math.max(1, Math.floor(canvas.clientWidth));
      const h = SCOPE_HEIGHT;
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
        ctx.fillStyle = '#08080a';
        ctx.fillRect(0, 0, w, h);
      }
    };
    fit();
    const ro = new ResizeObserver(fit);
    ro.observe(canvas);

    let raf = 0;
    const render = () => {
      raf = requestAnimationFrame(render);
      const w = canvas.width;
      const h = canvas.height;
      // Slow peak decay so a strong burst doesn't permanently dim the floor.
      peak = Math.max(1e-4, peak * 0.995);

      // Scroll left 1px, then paint the newest column on the right edge.
      ctx.drawImage(canvas, -1, 0);
      ctx.fillStyle = '#08080a';
      ctx.fillRect(w - 1, 0, 1, h);

      const col = pendingColumn;
      if (col) {
        const norm = 1 / (Math.log1p(peak) || 1);
        for (let b = 0; b < BINS; b += 1) {
          const v = Math.min(1, Math.log1p(col[b]!) * norm);
          if (v <= 0.02) continue;
          // Low freq at the bottom.
          const y0 = Math.round((1 - (b + 1) / BINS) * h);
          const y1 = Math.round((1 - b / BINS) * h);
          // Amber (--amber #ffb13c) with intensity-driven alpha + brightness.
          const a = 0.12 + 0.88 * v;
          const g = Math.round(140 + 60 * v);
          ctx.fillStyle = `rgba(255, ${g}, 60, ${a})`;
          ctx.fillRect(w - 1, y0, 1, Math.max(1, y1 - y0));
        }
      }
    };
    raf = requestAnimationFrame(render);

    return () => {
      unsubscribe();
      cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, [active]);

  return (
    <div className="deepcw-scope">
      <canvas ref={canvasRef} className="deepcw-scope-canvas" />
      {/* Static frame marking the 400–1200 Hz region the model decodes. */}
      <div
        className="deepcw-scope-band"
        style={{ top: `${bandTopPct}%`, height: `${bandBotPct - bandTopPct}%` }}
        aria-hidden="true"
      >
        <span className="deepcw-scope-band-label">decode band</span>
      </div>
      {!active && <div className="deepcw-scope-idle">spectrogram idle</div>}
    </div>
  );
}
