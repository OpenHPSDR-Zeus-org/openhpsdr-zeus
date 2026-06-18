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

import { useEffect, useRef, useState } from 'react';
import { BloomFill, BloomFilter, PeakTick } from './meters/render/svgChrome';
import { GaugeBezel } from './meters/render/GaugeBezel';
import { GlassDome } from './meters/render/GlassDome';
import { recessedWell } from './meters/render/recessedWell';
import { useGlidedFraction } from './meters/render/useGlidedDraw';

// RX scale: amateur-radio S-units. S9 = -73 dBm on HF. Each S-unit = 6 dB.
// Above S9 labelled in dB over S9 (+10, +20, +40, +60).
const S9_DBM = -73;
const DB_PER_S = 6;
const RX_MIN_DBM = S9_DBM - 9 * DB_PER_S; // S0 = -127 dBm
const RX_MAX_DBM = S9_DBM + 60;            // +60 over S9

type Tick = { pos: number; label: string; major: boolean };

const RX_TICKS: readonly Tick[] = (() => {
  const span = RX_MAX_DBM - RX_MIN_DBM;
  const at = (dbm: number) => (dbm - RX_MIN_DBM) / span;
  const ticks: Tick[] = [];
  // S-unit ticks (S0..S9)
  for (let s = 0; s <= 9; s++) {
    const dbm = RX_MIN_DBM + s * DB_PER_S;
    const major = s === 0 || s === 1 || s === 3 || s === 5 || s === 7 || s === 9;
    ticks.push({ pos: at(dbm), label: major ? `S${s}` : '', major });
  }
  // Over-S9 ticks
  for (const over of [10, 20, 40, 60]) {
    ticks.push({ pos: at(S9_DBM + over), label: `+${over}`, major: true });
  }
  return ticks;
})();

function rxFraction(dbm: number): number {
  const clamped = Math.max(RX_MIN_DBM, Math.min(RX_MAX_DBM, dbm));
  return (clamped - RX_MIN_DBM) / (RX_MAX_DBM - RX_MIN_DBM);
}

// TX scale: 0..maxW, linear. Ticks at 0, 25, 50, 75, 100%.
function txTicks(maxW: number): Tick[] {
  return [0, 0.25, 0.5, 0.75, 1].map((p) => ({
    pos: p,
    label: `${Math.round(p * maxW)}`,
    major: true,
  }));
}

export type SMeterProps =
  | {
      mode: 'rx';
      /** Current RX signal strength in dBm. */
      dbm: number;
    }
  | {
      mode: 'tx';
      /** Forward power in Watts. */
      watts: number;
      /** Max scale in Watts. Default 100. */
      maxWatts?: number;
    };

// Peak-hold decay time to zero, ms.
const PEAK_DECAY_MS = 1500;

export function SMeter(props: SMeterProps) {
  const isTx = props.mode === 'tx';
  const maxWatts = isTx ? props.maxWatts ?? 100 : 100;

  const fraction = isTx
    ? Math.max(0, Math.min(1, props.watts / maxWatts))
    : rxFraction(props.dbm);

  const ticks = isTx ? txTicks(maxWatts) : RX_TICKS;

  // Peak-hold: rises instantly with the signal, decays linearly.
  const [peak, setPeak] = useState(fraction);
  const peakAtRef = useRef<number>(performance.now());
  const rafRef = useRef<number | null>(null);

  useEffect(() => {
    if (fraction >= peak) {
      setPeak(fraction);
      peakAtRef.current = performance.now();
      return;
    }
    const tick = () => {
      const elapsed = performance.now() - peakAtRef.current;
      const decayed = Math.max(
        fraction,
        peak - (peak - fraction) * (elapsed / PEAK_DECAY_MS),
      );
      setPeak(decayed);
      if (decayed > fraction) {
        rafRef.current = requestAnimationFrame(tick);
      }
    };
    rafRef.current = requestAnimationFrame(tick);
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
    };
  }, [fraction, peak]);

  // 60 Hz display-side glide for the amber bar — the DRAWN width springs
  // toward the live fraction on the shared draw-bus, written imperatively as a
  // scaleX on the fill <g> (no setState in the hot path). The peak marker is
  // NEVER glided — it stays on its own instant rAF latch above (#FFA028
  // signal-amber semantics + alpha ramp are untouched).
  const fillGroupRef = useRef<SVGGElement | null>(null);
  const glide = useGlidedFraction(fraction, {
    onDraw: (drawn) => {
      const g = fillGroupRef.current;
      if (g) g.setAttribute('transform', `scale(${Math.max(0, Math.min(1, drawn)).toFixed(4)} 1)`);
    },
  });
  const initialScale = Math.max(0, Math.min(1, glide.read())).toFixed(4);

  const valueLabel = isTx
    ? `${props.watts.toFixed(1)} W`
    : formatRxLabel(props.dbm);

  const label = isTx ? 'PWR' : 'RX';
  const badge = isTx ? 'TX' : null;

  return (
    <div
      role="meter"
      aria-label={isTx ? 'Transmit power' : 'Signal strength'}
      aria-valuemin={0}
      aria-valuemax={isTx ? maxWatts : Math.round(RX_MAX_DBM)}
      aria-valuenow={isTx ? Math.round(props.watts) : Math.round(props.dbm)}
      className="relative flex select-none items-stretch gap-2 bg-neutral-950/90 px-3 py-2 font-mono text-xs"
    >
      <div className="flex w-10 flex-col items-start justify-between pt-0.5 pb-1">
        <span className="uppercase tracking-widest text-neutral-400">
          {label}
        </span>
        <span className="text-[10px] text-neutral-500">
          {isTx ? 'W' : 'dBm'}
        </span>
      </div>

      <div className="relative flex-1">
        {/* Track — recessed machined well + warm amber halo, matching the
            immersive gauges and the TX-stage rows. */}
        <div
          className="relative h-6 overflow-hidden"
          style={recessedWell({ radius: 3, warmHalo: true, glow: true })}
        >
          {/* Fill — single-hue amber ramp matching the panadapter trace
              (#FFA028, see src/gl/panadapter.ts TRACE_R/G/B; the sanctioned
              signal amber). Rendered bloom-behind-crisp under a domed glass +
              drifting sheen, framed by a machined bezel ring. Alpha rises with
              signal: dim at S0, full-bright past S9 — no hue shift. The fill
              width is driven at 60 Hz by an imperative scaleX on the fill <g>;
              the PEAK tick latches instantly from `peak`. */}
          <svg
            aria-hidden
            viewBox="0 0 100 24"
            preserveAspectRatio="none"
            className="absolute inset-0"
            style={{ width: '100%', height: '100%', display: 'block' }}
          >
            <defs>
              <BloomFilter id="smeter-blur" region={['-2%', '-40%', '104%', '180%']} />
              <linearGradient id="smeter-fill" x1="0" y1="0" x2="100" y2="0" gradientUnits="userSpaceOnUse">
                <stop offset="0" stopColor="#FFA028" stopOpacity="0.18" />
                <stop offset="0.5" stopColor="#FFA028" stopOpacity="0.55" />
                <stop offset="1" stopColor="#FFA028" stopOpacity="1" />
              </linearGradient>
              {/* vertical volume gradient — cylindrical shading over the bar */}
              <linearGradient id="smeter-vol" x1="0" y1="0" x2="0" y2="24" gradientUnits="userSpaceOnUse">
                <stop offset="0" stopColor="var(--meter-fill-hot)" />
                <stop offset="0.18" stopColor="rgba(255,255,255,0.10)" />
                <stop offset="0.5" stopColor="rgba(0,0,0,0)" />
                <stop offset="0.82" stopColor="rgba(0,0,0,0.18)" />
                <stop offset="1" stopColor="var(--meter-fill-base)" />
              </linearGradient>
            </defs>

            {/* LED-ladder ground behind the fill */}
            <g opacity={0.45}>
              {Array.from({ length: 9 }).map((_, i) => (
                <line
                  key={`smled-${i}`}
                  x1={(i + 1) * 10}
                  y1={0}
                  x2={(i + 1) * 10}
                  y2={24}
                  stroke="rgba(255,255,255,0.05)"
                  strokeWidth={0.6}
                  vectorEffect="non-scaling-stroke"
                />
              ))}
            </g>

            {/* Live amber fill — unit-width, scaled in X by the glided draw. */}
            <g ref={fillGroupRef} transform={`scale(${initialScale} 1)`}>
              <BloomFill
                shape={{ kind: 'rect', x: 0, y: 0, width: 100, height: 24 }}
                fill="url(#smeter-fill)"
                filterId="smeter-blur"
              />
              <rect x={0} y={0} width={100} height={24} fill="url(#smeter-vol)" />
              {/* bright amber leading edge */}
              <rect
                x={97}
                y={0}
                width={3}
                height={24}
                fill="#FFA028"
                opacity={0.9}
                style={{ filter: 'drop-shadow(0 0 3px #FFA028)' }}
              />
            </g>

            {/* domed glass + drifting sheen */}
            <GlassDome defsId="smeter" x={0} y={0} width={100} height={24} rx={2} />

            {/* Peak-hold marker — glowing tick, latched instantly. */}
            <PeakTick
              x1={peak * 100}
              y1={-1}
              x2={peak * 100}
              y2={25}
              stroke="#fff"
              strokeWidth={2.2}
              opacity={0.9}
              nonScaling
            />

            {/* machined bezel ring */}
            <GaugeBezel variant="rect" defsId="smeter" x={0} y={0} width={100} height={24} rx={2} thickness={2.4} nonScaling />
          </svg>
          {/* S9 reference marker for RX — thin amber line, not red. */}
          {!isTx && (
            <div
              aria-hidden
              className="absolute inset-y-0 w-px bg-amber-300/40"
              style={{ left: `${rxFraction(S9_DBM) * 100}%` }}
            />
          )}
          {/* TX badge inside the track, top-right */}
          {badge && (
            <span className="absolute right-1 top-0.5 rounded-sm bg-red-500/20 px-1 text-[10px] font-bold tracking-wider text-red-300 ring-1 ring-red-400/40">
              {badge}
            </span>
          )}
        </div>

        {/* Tick scale */}
        <div className="relative mt-1 h-3 text-[9px] text-neutral-400">
          {ticks.map((t, i) => (
            <div
              key={`${t.label}-${i}`}
              className="absolute top-0 flex -translate-x-1/2 flex-col items-center"
              style={{ left: `${t.pos * 100}%` }}
            >
              <div
                className={
                  'w-px ' +
                  (t.major ? 'h-1.5 bg-neutral-500' : 'h-1 bg-neutral-700')
                }
              />
              {t.label && (
                <div className="leading-none">{t.label}</div>
              )}
            </div>
          ))}
        </div>
      </div>

      <div className="flex w-20 flex-col items-end justify-between pt-0.5 pb-1">
        <span className="text-neutral-200">{valueLabel}</span>
        {!isTx && (
          <span className="text-[10px] text-neutral-500">
            {sUnitLabel(props.dbm)}
          </span>
        )}
      </div>
    </div>
  );
}

function formatRxLabel(dbm: number): string {
  return `${dbm.toFixed(0)} dBm`;
}

function sUnitLabel(dbm: number): string {
  if (dbm >= S9_DBM) {
    const over = dbm - S9_DBM;
    return `S9+${over.toFixed(0)}`;
  }
  const s = Math.max(0, Math.min(9, Math.round((dbm - RX_MIN_DBM) / DB_PER_S)));
  return `S${s}`;
}
