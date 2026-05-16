// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Big semicircle "final output" gauge. Lift-and-shift of the design
// prototype's `.arc` SVG (Immersive Meters.html) recreated as a
// presentational React component, then generalised to two modes:
//
//   mode='dbfs'  — log-style audio dBFS (-60..+6) with red 0 dB tick,
//                  pegs the "over" red readout above 0 dBFS. Used for
//                  WDSP modulator-output level meters.
//
//   mode='watts' — linear forward-watts (0..maxWatts), with five even
//                  tick steps. Used for the operator-facing "what's on
//                  the air?" power meter — the meter that actually
//                  scales with the radio's drive %, unlike WDSP's
//                  digital OUT meter which sees the modulator at full
//                  scale during TUNE regardless of RF power.
//
// Both modes render the same chrome (gradient fill, ambient glow,
// needle, hub, peak pip, mono readout). The needle pivots from the hub
// in viewBox units via SVG `transform="rotate(angle cx cy)"` — a CSS
// transform-origin would mismatch once the SVG scales to the tile.

import type { CSSProperties } from 'react';
import { dbToFrac, fmtDb, isSilent } from './dbScale';
import { usePeakHoldFrac } from './usePeakHold';
import { immersiveZoneTickColor, type ZoneTick } from '../meters/meterCatalog';

interface CommonProps {
  /** Section/label text — top-left chip. */
  label: string;
  /** Subscript chip on top-right (e.g. "dBFS · RMS" or "Watts · PEP"). */
  units?: string;
  /** Stable id prefix for SVG `<defs>` so multiple arcs on a page don't
   *  collide on `id="arcFill"`. Required. */
  defsId: string;
  /** Optional green/amber/red tick marks at zone-level boundaries. Always
   *  visible at idle; render INSIDE the rim (R-12..R-6) so the live fill
   *  stroke at radius R never occludes them. The configurable Meters Panel
   *  passes ticks derived from each reading's `zones`/`warnAt`/`dangerAt`;
   *  the immersive TX Stage Meters panel passes none (it relies on the
   *  rim gradient + readout colour for the "you're past the rail" cue). */
  zoneTicks?: ReadonlyArray<ZoneTick>;
}

interface DbfsProps extends CommonProps {
  mode: 'dbfs';
  /** Live value in dBFS. ≤ −200 / non-finite renders as bypassed. */
  valueDb: number;
}

interface WattsProps extends CommonProps {
  mode: 'watts';
  /** Live forward power in watts. */
  watts: number;
  /** Top of axis — typically the connected board's MaxPowerWatts. */
  maxWatts: number;
}

interface SwrProps extends CommonProps {
  mode: 'swr';
  /** Live SWR ratio. ≤ 1.0 / non-finite is treated as silent. */
  ratio: number;
}

export type BigArcProps = DbfsProps | WattsProps | SwrProps;

const CX = 120;
const CY = 124;
const R = 92;
const ARC_LEN = Math.PI * R;

function pointAt(fraction: number, radius: number): { x: number; y: number } {
  // 180° (left) → 360° (right): a half-turn anchored at the bottom.
  const angleDeg = 180 + 180 * fraction;
  const a = (angleDeg * Math.PI) / 180;
  return {
    x: CX + Math.cos(a) * radius,
    y: CY + Math.sin(a) * radius,
  };
}

interface AxisTick {
  /** Position along the arc, 0..1 (left → right). */
  frac: number;
  /** Tick label; empty string draws a tick mark with no label. */
  label: string;
  /** Highlighted tick (e.g. red 0 dB marker). */
  highlight?: boolean;
}

const DBFS_TICKS: ReadonlyArray<AxisTick> = [
  { frac: dbToFrac(-60), label: '60' },
  { frac: dbToFrac(-40), label: '40' },
  { frac: dbToFrac(-20), label: '20' },
  { frac: dbToFrac(-10), label: '10' },
  { frac: dbToFrac(-6), label: '6' },
  { frac: dbToFrac(-3), label: '3' },
  { frac: dbToFrac(0), label: '0', highlight: true },
  { frac: dbToFrac(3), label: '+3' },
  { frac: dbToFrac(6), label: '' },
];

/** Format a watt value tick: small radios get sub-W decimals (HL2 1.0 W),
 *  big radios round to whole watts (G2-1K 200, 400, 600...). */
function fmtWattsTick(watts: number, max: number): string {
  if (max <= 0) return '0';
  if (max < 10) return watts.toFixed(1);
  return Math.round(watts).toString();
}

function wattsTicks(maxWatts: number): ReadonlyArray<AxisTick> {
  const safeMax = isFinite(maxWatts) && maxWatts > 0 ? maxWatts : 100;
  return Array.from({ length: 6 }, (_, i) => {
    const w = (i / 5) * safeMax;
    return {
      frac: i / 5,
      label: fmtWattsTick(w, safeMax),
      // Red highlight on the rated-max tick — the "you're at the rail" cue
      // mirrors the dBFS axis's red 0 dB highlight.
      highlight: i === 5,
    };
  });
}

// SWR axis: linear 1.0..3.0+, ticks at 1.0/1.5/2.0/2.5/3.0+, with the 2.0
// tick highlighted red (matches the backend SWR alert trip threshold).
const SWR_MIN = 1.0;
const SWR_MAX = 3.0;
const SWR_TICKS: ReadonlyArray<AxisTick> = [
  { frac: 0.0, label: '1.0' },
  { frac: 0.25, label: '1.5' },
  { frac: 0.5, label: '2.0', highlight: true },
  { frac: 0.75, label: '2.5' },
  { frac: 1.0, label: '3+' },
];

function swrToFrac(ratio: number): number {
  if (!isFinite(ratio) || ratio < SWR_MIN) return 0;
  return Math.max(0, Math.min(1, (ratio - SWR_MIN) / (SWR_MAX - SWR_MIN)));
}

interface ResolvedAxis {
  /** Live value as 0..1 along the arc. */
  liveFrac: number;
  /** Whether the meter is silent / bypassed (no needle, em-dash readout). */
  silent: boolean;
  /** Whether the live value is at-or-past the danger limit (0 dBFS or ratedW). */
  over: boolean;
  /** Tick definitions for the chosen axis. */
  ticks: ReadonlyArray<AxisTick>;
  /** Big readout text (e.g. "−18.4" or "5.4"). */
  readoutText: string;
  /** Small unit suffix shown next to the readout (e.g. "dBFS" or "W"). */
  readoutUnit: string;
  /** Live numeric value used by the peak-hold hook (in axis-native units). */
  rawValue: number;
  /** Function mapping a live value to a 0..1 fraction (passed to peak-hold). */
  toFrac: (v: number) => number;
}

function resolveAxis(props: BigArcProps): ResolvedAxis {
  if (props.mode === 'dbfs') {
    const silent = isSilent(props.valueDb);
    const liveFrac = silent ? 0 : dbToFrac(props.valueDb);
    return {
      liveFrac,
      silent,
      over: !silent && props.valueDb > 0,
      ticks: DBFS_TICKS,
      readoutText: silent ? '—' : fmtDb(props.valueDb),
      readoutUnit: 'dBFS',
      rawValue: props.valueDb,
      toFrac: dbToFrac,
    };
  }
  if (props.mode === 'swr') {
    const { ratio } = props;
    const finite = isFinite(ratio) && ratio >= SWR_MIN;
    const liveFrac = finite ? swrToFrac(ratio) : 0;
    return {
      liveFrac,
      silent: !finite,
      // "over" tints the readout red past 2.0:1 — matches the backend
      // SWR alert trip threshold and the highlighted 2.0 tick.
      over: finite && ratio >= 2.0,
      ticks: SWR_TICKS,
      readoutText: finite ? ratio.toFixed(2) : '—',
      readoutUnit: ':1',
      rawValue: finite ? ratio : Number.NEGATIVE_INFINITY,
      toFrac: swrToFrac,
    };
  }
  const { watts, maxWatts } = props;
  const finite = isFinite(watts) && watts > 0;
  const safeMax = isFinite(maxWatts) && maxWatts > 0 ? maxWatts : 100;
  const liveFrac = finite ? Math.max(0, Math.min(1, watts / safeMax)) : 0;
  const decimals = safeMax < 10 ? 2 : 1;
  return {
    liveFrac,
    silent: !finite,
    over: liveFrac >= 1.0,
    ticks: wattsTicks(safeMax),
    readoutText: finite ? watts.toFixed(decimals) : '—',
    readoutUnit: 'W',
    rawValue: finite ? watts : Number.NEGATIVE_INFINITY,
    toFrac: (v) => (isFinite(v) && v > 0 ? Math.max(0, Math.min(1, v / safeMax)) : 0),
  };
}

export function BigArc(props: BigArcProps) {
  const axis = resolveAxis(props);
  const peakFrac = usePeakHoldFrac(axis.rawValue, axis.toFrac);

  const fillLen = ARC_LEN * axis.liveFrac;
  const fillDash = `${fillLen.toFixed(1)} ${(ARC_LEN + 5).toFixed(1)}`;
  const needleAngle = -90 + 180 * axis.liveFrac;
  const peakPoint = pointAt(peakFrac, R);

  const fillGradId = `${props.defsId}-fill`;
  const glowGradId = `${props.defsId}-glow`;
  const blurFilterId = `${props.defsId}-blur`;
  const units = props.units ?? axis.readoutUnit;

  const cardStyle: CSSProperties = {
    position: 'relative',
    aspectRatio: '1.55 / 1',
    borderRadius: 7,
    // Warm-cream "lamp glow" rising from the bottom of the gauge face —
    // simulates an incandescent bulb illuminating the instrument from
    // below. Layered: bottom-anchored cream radial → mid pale-yellow
    // radial → dark linear panel base. The decorative bloom blob is
    // painted via cardBloomStyle below the SVG.
    background:
      'radial-gradient(80% 95% at 50% 95%, var(--immersive-lamp-bloom-1), var(--immersive-lamp-bloom-2) 45%, transparent 72%),' +
      ' radial-gradient(60% 60% at 50% 70%, var(--immersive-lamp-bloom-3), transparent 65%),' +
      ' linear-gradient(180deg, var(--immersive-lamp-well-top) 0%, var(--immersive-lamp-well-bot) 100%)',
    border: '1px solid var(--immersive-lamp-border)',
    boxShadow:
      'inset 0 1px 0 var(--immersive-lamp-rim), inset 0 -22px 40px rgba(255,240,180,0.05), inset 0 0 50px rgba(0,0,0,0.55)',
    overflow: 'hidden',
  };
  // Decorative bottom-blob bloom — sits behind the SVG and softens the
  // lamp glow so the cream tone fades up the dial face rather than
  // banding sharply at 50% height.
  const cardBloomStyle: CSSProperties = {
    position: 'absolute',
    left: '50%',
    bottom: '-30%',
    width: '120%',
    height: '90%',
    transform: 'translateX(-50%)',
    background:
      'radial-gradient(50% 50% at 50% 50%, var(--immersive-lamp-bloom-blob), transparent 70%)',
    pointerEvents: 'none',
    filter: 'blur(2px)',
  };
  const labelStyle: CSSProperties = {
    position: 'absolute',
    top: 9,
    left: 12,
    fontSize: 9,
    letterSpacing: '0.18em',
    textTransform: 'uppercase',
    color: 'var(--immersive-lamp-label)',
    fontWeight: 700,
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    zIndex: 1,
  };
  const pinStyle: CSSProperties = {
    width: 5,
    height: 5,
    borderRadius: '50%',
    background: 'var(--immersive-lamp-pin)',
    boxShadow: '0 0 8px var(--immersive-lamp-pin-glow)',
  };
  const unitsStyle: CSSProperties = {
    position: 'absolute',
    top: 9,
    right: 12,
    fontFamily: 'var(--font-mono)',
    fontSize: 9,
    color: 'var(--immersive-lamp-units)',
    letterSpacing: '0.10em',
    textTransform: 'uppercase',
    zIndex: 1,
  };
  const readoutStyle: CSSProperties = {
    position: 'absolute',
    left: 0,
    right: 0,
    bottom: 10,
    textAlign: 'center',
    fontFamily: 'var(--font-mono)',
    fontSize: 24,
    fontWeight: 600,
    letterSpacing: '-0.01em',
    fontVariantNumeric: 'tabular-nums',
    lineHeight: 1,
    color: axis.over ? '#ffb8a4' : 'var(--immersive-lamp-readout)',
    textShadow: axis.over
      ? '0 0 14px var(--immersive-tx-glow)'
      : '0 0 14px var(--immersive-lamp-readout-glow)',
  };
  const unitSpanStyle: CSSProperties = {
    color: 'var(--immersive-lamp-corner-em)',
    fontSize: 10.5,
    fontWeight: 500,
    marginLeft: 4,
    letterSpacing: '0.05em',
  };

  return (
    <div style={cardStyle} aria-hidden="true">
      <div style={cardBloomStyle} />
      <span style={labelStyle}>
        <span style={pinStyle} />
        {props.label}
      </span>
      <span style={unitsStyle}>{units}</span>

      <svg
        viewBox="0 0 240 150"
        preserveAspectRatio="xMidYMid meet"
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block' }}
      >
        <defs>
          <linearGradient id={fillGradId} x1="0" x2="1" y1="0" y2="0">
            <stop offset="0" stopColor="var(--immersive-good)" />
            <stop offset="0.55" stopColor="var(--immersive-good)" />
            <stop offset="0.78" stopColor="var(--immersive-warn)" />
            <stop offset="1" stopColor="var(--immersive-tx)" />
          </linearGradient>
          <radialGradient id={glowGradId} cx="50%" cy="100%" r="80%">
            <stop offset="0" stopColor="#ffffff" stopOpacity="0.10" />
            <stop offset="1" stopColor="#ffffff" stopOpacity="0" />
          </radialGradient>
          <filter id={blurFilterId} x="-40%" y="-40%" width="180%" height="180%">
            <feGaussianBlur stdDeviation="3" />
          </filter>
        </defs>

        {/* ambient ground glow — pale white over the warm-cream lamp wash */}
        <ellipse cx={CX} cy={135} rx={110} ry={40} fill={`url(#${glowGradId})`} />

        {/* background arc — soft track */}
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke="rgba(255,255,255,0.06)"
          strokeWidth={14}
          strokeLinecap="round"
        />
        {/* track shadow */}
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke="rgba(0,0,0,0.4)"
          strokeWidth={10}
        />

        {/* active fill — bloomed copy + crisp copy on top */}
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke={`url(#${fillGradId})`}
          strokeWidth={9}
          strokeLinecap="round"
          strokeDasharray={fillDash}
          filter={`url(#${blurFilterId})`}
          opacity={0.85}
        />
        <path
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke={`url(#${fillGradId})`}
          strokeWidth={6}
          strokeLinecap="round"
          strokeDasharray={fillDash}
        />

        {/* zone-transition ticks — coloured perpendicular lines at the
            inner-rim band (R-12..R-6). Rendered before axis ticks so the
            white axis ticks paint over them in the rare case where a zone
            boundary coincides with an axis tick. */}
        {props.zoneTicks && props.zoneTicks.length > 0 && (
          <g strokeLinecap="round">
            {props.zoneTicks.map((zt, i) => {
              const inner = pointAt(zt.frac, R - 12);
              const outer = pointAt(zt.frac, R - 6);
              return (
                <line
                  key={`zt-${i}`}
                  x1={inner.x.toFixed(1)}
                  y1={inner.y.toFixed(1)}
                  x2={outer.x.toFixed(1)}
                  y2={outer.y.toFixed(1)}
                  stroke={immersiveZoneTickColor(zt.level)}
                  strokeWidth={2.2}
                />
              );
            })}
          </g>
        )}

        {/* ticks — warm-cream lamp tone, except `highlight` ticks (e.g.
            rated-max / 0 dB / SWR 2.0) which keep the tx red as a "hot"
            cue. */}
        <g strokeWidth={1}>
          {axis.ticks.map((t, i) => {
            const inner = pointAt(t.frac, R - 9);
            const outer = pointAt(t.frac, R + 5);
            const stroke = t.highlight
              ? 'var(--immersive-tx)'
              : 'var(--immersive-lamp-tick)';
            const sw = t.highlight ? 1.6 : 1;
            return (
              <line
                key={`t-${i}`}
                x1={inner.x.toFixed(1)}
                y1={inner.y.toFixed(1)}
                x2={outer.x.toFixed(1)}
                y2={outer.y.toFixed(1)}
                stroke={stroke}
                strokeWidth={sw}
              />
            );
          })}
        </g>
        <g
          fontFamily="var(--font-mono)"
          fontSize={8}
          textAnchor="middle"
        >
          {axis.ticks
            .filter((t) => t.label !== '')
            .map((t, i) => {
              const lp = pointAt(t.frac, R + 15);
              return (
                <text
                  key={`tl-${i}`}
                  x={lp.x.toFixed(1)}
                  y={(lp.y + 3).toFixed(1)}
                  fill={t.highlight ? 'var(--immersive-tx)' : 'var(--immersive-lamp-label)'}
                >
                  {t.label}
                </text>
              );
            })}
        </g>

        {/* peak-hold pip on the rim — warm-cream pearl with cream halo */}
        {!axis.silent && peakFrac > 0 && (
          <circle
            cx={peakPoint.x.toFixed(1)}
            cy={peakPoint.y.toFixed(1)}
            r={3}
            fill="#fff"
            stroke="var(--immersive-lamp-pin)"
            strokeWidth={1}
            style={{ filter: 'drop-shadow(0 0 6px var(--immersive-lamp-pin))' }}
          />
        )}

        {/* needle — warm-cream tapered ribbon over a pale-yellow centerline.
            Pivots around the hub centre (CX, CY) in viewBox units. */}
        {!axis.silent && (
          <g transform={`rotate(${needleAngle.toFixed(2)} ${CX} ${CY})`}>
            <line
              x1={CX}
              y1={CY}
              x2={CX}
              y2={36}
              stroke="var(--immersive-lamp-needle)"
              strokeWidth={2}
              strokeLinecap="round"
            />
            <line
              x1={CX}
              y1={CY}
              x2={CX}
              y2={50}
              stroke="var(--immersive-lamp-needle-bri)"
              strokeWidth={0.8}
              opacity={0.65}
            />
          </g>
        )}

        {/* hub — dark cap with cream rim and a warm pin centre */}
        <circle
          cx={CX}
          cy={CY}
          r={9}
          fill="#15151a"
          stroke="rgba(245,240,210,0.38)"
          strokeWidth={1.4}
        />
        <circle
          cx={CX}
          cy={CY}
          r={3}
          fill="var(--immersive-lamp-needle)"
          style={{ filter: 'drop-shadow(0 0 5px var(--immersive-lamp-hub-glow))' }}
        />
      </svg>

      <div style={readoutStyle}>
        {axis.readoutText}
        <span style={unitSpanStyle}>{axis.readoutUnit}</span>
      </div>
    </div>
  );
}
