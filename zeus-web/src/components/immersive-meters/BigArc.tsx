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

import { useRef, type CSSProperties } from 'react';
import { dbToFrac, fmtDb, isSilent } from './dbScale';
import { usePeakHoldFrac } from './usePeakHold';
import { immersiveZoneTickColor, type ZoneTick } from '../meters/meterCatalog';
import { BloomFilter, PeakPip, prefixDefs } from '../meters/render/svgChrome';
import { GaugeBezel } from '../meters/render/GaugeBezel';
import { GlassDome } from '../meters/render/GlassDome';
import { lampCardStyle } from '../meters/render/lampCard';
import { useGlidedFraction } from '../meters/render/useGlidedDraw';

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

// Build the dash string for a given 0..1 fraction of the arc.
function dashFor(frac: number): string {
  const len = ARC_LEN * Math.max(0, Math.min(1, frac));
  return `${len.toFixed(1)} ${(ARC_LEN + 5).toFixed(1)}`;
}

export function BigArc(props: BigArcProps) {
  const axis = resolveAxis(props);
  const peakFrac = usePeakHoldFrac(axis.rawValue, axis.toFrac);

  // 60 Hz display-side glide for the live arc + needle. We glide liveFrac on
  // the shared draw-bus and write the fill dash + needle rotation imperatively
  // to element refs each frame, so the arc/needle move at up to 60 Hz between
  // the ~30 Hz panel re-render cadence — without a setState in the hot path.
  // Peak (peakFrac / peakPoint) is NEVER glided; it stays instant.
  const fillCrispRef = useRef<SVGPathElement | null>(null);
  const fillBloomRef = useRef<SVGPathElement | null>(null);
  const needleRef = useRef<SVGGElement | null>(null);
  const headRef = useRef<SVGCircleElement | null>(null);
  const headGlowRef = useRef<SVGCircleElement | null>(null);
  const glide = useGlidedFraction(axis.silent ? 0 : axis.liveFrac, {
    onDraw: (drawn) => {
      const dash = dashFor(drawn);
      fillCrispRef.current?.setAttribute('stroke-dasharray', dash);
      fillBloomRef.current?.setAttribute('stroke-dasharray', dash);
      const ang = (-90 + 180 * Math.max(0, Math.min(1, drawn))).toFixed(2);
      needleRef.current?.setAttribute('transform', `rotate(${ang} ${CX} ${CY})`);
      // Glowing head pip rides the live arc tip.
      const hp = pointAt(Math.max(0, Math.min(1, drawn)), R);
      headRef.current?.setAttribute('cx', hp.x.toFixed(1));
      headRef.current?.setAttribute('cy', hp.y.toFixed(1));
      headGlowRef.current?.setAttribute('cx', hp.x.toFixed(1));
      headGlowRef.current?.setAttribute('cy', hp.y.toFixed(1));
    },
  });

  const drawn0 = axis.silent ? 0 : glide.read();
  const fillDash = dashFor(drawn0);
  const needleAngle = -90 + 180 * Math.max(0, Math.min(1, drawn0));
  const headPoint0 = pointAt(Math.max(0, Math.min(1, drawn0)), R);
  const peakPoint = pointAt(peakFrac, R);
  const isSwr = props.mode === 'swr';

  const fillGradId = prefixDefs(props.defsId, 'fill');
  const glowGradId = prefixDefs(props.defsId, 'glow');
  const blurFilterId = prefixDefs(props.defsId, 'blur');
  const glassClipId = prefixDefs(props.defsId, 'glassclip');
  const units = props.units ?? axis.readoutUnit;

  // Shared arc geometry path used by the track / bezel / channel strokes.
  const arcPath = `M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`;

  // Warm-cream "lamp glow" rising from the bottom of the gauge face —
  // simulates an incandescent bulb illuminating the instrument from below.
  // The illuminated-surface recipe (cream radials over a dark well + inset
  // rim/vignette) now lives in the shared lampCard factory; this card just
  // adds its own layout (aspect / position / overflow). The decorative
  // bloom blob is painted via cardBloomStyle below the SVG.
  const cardStyle: CSSProperties = {
    ...lampCardStyle('arc'),
    position: 'relative',
    aspectRatio: '1.55 / 1',
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
          <BloomFilter id={blurFilterId} />
          {/* clip the glass dome to the dial bounding box so the specular
              cover only sits over the instrument face, not the readout. */}
          <clipPath id={glassClipId}>
            <rect x={20} y={24} width={200} height={104} rx={10} />
          </clipPath>
        </defs>

        {/* ambient ground glow — pale white over the warm-cream lamp wash */}
        <ellipse cx={CX} cy={135} rx={110} ry={40} fill={`url(#${glowGradId})`} />

        {/* machined bezel ring framing the arc — the chrome edge that makes the
            gauge sit IN the panel. Drawn first (widest) so the track + channel
            nest inside it. */}
        <GaugeBezel variant="arc" defsId={props.defsId} d={arcPath} thickness={16} />

        {/* recessed track channel — a dark inset core stroked inside the bezel
            so the arc reads as a machined groove, not a flat soft track. */}
        <path
          d={arcPath}
          fill="none"
          stroke="var(--immersive-arc-track-rim)"
          strokeWidth={11}
          strokeLinecap="round"
        />
        <path
          d={arcPath}
          fill="none"
          stroke="var(--arc-channel-core)"
          strokeWidth={9}
          strokeLinecap="round"
        />
        <path
          d={arcPath}
          fill="none"
          stroke="var(--immersive-arc-track-shadow)"
          strokeWidth={7}
        />

        {/* SWR danger-zone band — a low-alpha red arc painted UNDER the fill
            from 2.0:1 → 3.0+ so a climbing SWR reads as entering an alarm
            region even before the needle gets there. SWR mode only. */}
        {isSwr && (
          <path
            d={arcPath}
            fill="none"
            stroke="var(--arc-danger-band)"
            strokeWidth={9}
            strokeLinecap="butt"
            strokeDasharray={`${(ARC_LEN * 0.5).toFixed(1)} ${(ARC_LEN + 5).toFixed(1)}`}
            strokeDashoffset={`-${(ARC_LEN * 0.5).toFixed(1)}`}
          />
        )}

        {/* active fill — bloomed copy + crisp copy on top. Inlined (rather
            than via the BloomFill kit) so the two paths can carry refs the
            60 Hz glide writes stroke-dasharray to imperatively. */}
        <path
          ref={fillBloomRef}
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
          ref={fillCrispRef}
          d={`M 28 ${CY} A ${R} ${R} 0 0 1 212 ${CY}`}
          fill="none"
          stroke={`url(#${fillGradId})`}
          strokeWidth={6}
          strokeLinecap="round"
          strokeDasharray={fillDash}
        />

        {/* glowing leading-edge head pip — a bright lit bead riding the live
            arc tip, hotter on SWR-over. Latches with the glide, not the peak. */}
        {!axis.silent && (
          <>
            <circle
              ref={headGlowRef}
              cx={headPoint0.x.toFixed(1)}
              cy={headPoint0.y.toFixed(1)}
              r={6}
              fill={axis.over ? 'var(--immersive-tx)' : 'var(--meter-fill-glow)'}
              opacity={0.55}
              style={{ filter: 'blur(2.5px)' }}
            />
            <circle
              ref={headRef}
              cx={headPoint0.x.toFixed(1)}
              cy={headPoint0.y.toFixed(1)}
              r={2.6}
              fill="#fff"
              opacity={0.95}
              style={{
                filter: `drop-shadow(0 0 4px ${axis.over ? 'var(--immersive-tx-glow)' : 'var(--meter-fill-glow)'})`,
              }}
            />
          </>
        )}

        {/* specular glass cover over the dial face — wet-glass highlight +
            drifting sheen, clipped to the dial bounding box. Sits over the
            fill + ticks but below the needle/hub so the operator reads the
            needle THROUGH the glass. */}
        <g clipPath={`url(#${glassClipId})`}>
          <GlassDome defsId={props.defsId} x={20} y={24} width={200} height={104} rx={10} sheenWidthFrac={0.14} />
        </g>

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
          <PeakPip cx={peakPoint.x.toFixed(1)} cy={peakPoint.y.toFixed(1)} r={3} />
        )}

        {/* needle — warm-cream tapered ribbon over a pale-yellow centerline.
            Pivots around the hub centre (CX, CY) in viewBox units. */}
        {!axis.silent && (
          <g ref={needleRef} transform={`rotate(${needleAngle.toFixed(2)} ${CX} ${CY})`}>
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
