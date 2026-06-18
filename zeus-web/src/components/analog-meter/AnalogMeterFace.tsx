// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Pure-SVG analog dial: concentric scale arcs, ticks, labels, and the
// moving-coil needle (with shadow + counterweight + bright tip + peak-hold
// ghost). All colors come from tokens.css; no raw hex.
//
// Translated from the design handoff (display/project/s-meter-face.jsx) and
// adapted to the Zeus palette: the warm-red needle uses --tx, the active
// arc + +dB region uses --accent, base chrome uses --fg-* / --bg-* / --line.

import { Fragment, useRef } from 'react';
import {
  FACE,
  pt,
  arcPath,
  normToDeg,
  SCALES,
  type ScaleDef,
  type ScaleId,
} from './analogMeterShared';
import { BloomFilter, prefixDefs } from '../meters/render/svgChrome';
import { GaugeBezel } from '../meters/render/GaugeBezel';
import { useGlidedFraction } from '../meters/render/useGlidedDraw';

interface ScaleArcProps {
  scale: ScaleDef;
  radius: number;
  active: boolean;
  enabled: boolean;
  peakValueN: number | null;
}

function ScaleArc({ scale, radius, active, enabled, peakValueN }: ScaleArcProps) {
  if (!enabled) return null;

  const half = FACE.sweep / 2;
  const a0 = -half;
  const a1 = +half;

  const trackColor = active ? 'var(--accent)' : 'var(--fg-2)';
  const trackOpacity = active ? 0.9 : 0.45;
  const trackWidth = active ? 2 : 1;
  const labelColor = active ? 'var(--fg-0)' : 'var(--fg-1)';
  const labelOpacity = active ? 1 : 0.85;

  const tickAngle = (v: number) => normToDeg(scale.n(v));
  const peakDeg = active && peakValueN != null ? normToDeg(peakValueN) : null;
  const isS = scale.id === 's';
  const arcBlurId = prefixDefs(`amface-${scale.id}`, 'arcblur');

  return (
    <g>
      {/* inner-groove dark inset arc behind the lit track — a recessed
          channel the illuminated tube sits inside. */}
      <path
        d={arcPath(FACE.cx, FACE.cy, radius, a0, a1)}
        stroke="rgba(0,0,0,0.55)"
        strokeWidth={active ? 12 : 7}
        fill="none"
        strokeLinecap="round"
      />
      <path
        d={arcPath(FACE.cx, FACE.cy, radius, a0, a1)}
        stroke={trackColor}
        strokeOpacity={trackOpacity}
        strokeWidth={trackWidth}
        fill="none"
      />

      {/* Illuminated active arc tube — a wide blurred copy under a crisp
          --accent stroke (bloom-behind-crisp), so the active scale reads as a
          lit channel instead of a 2px hairline. Drawn full-span at low alpha
          as the "lit rail," brightest near the live end via the peak arc. */}
      {active && (
        <g opacity={0.9}>
          <defs>
            <BloomFilter id={arcBlurId} stdDeviation={5} region={['-30%', '-30%', '160%', '160%']} />
          </defs>
          {/* wide soft glow rail */}
          <path
            d={arcPath(FACE.cx, FACE.cy, radius, a0, a1)}
            stroke="var(--accent)"
            strokeOpacity={0.4}
            strokeWidth={11}
            fill="none"
            strokeLinecap="round"
            filter={`url(#${arcBlurId})`}
          />
          {/* crisp lit tube */}
          <path
            d={arcPath(FACE.cx, FACE.cy, radius, a0, a1)}
            stroke="var(--accent)"
            strokeOpacity={0.85}
            strokeWidth={5}
            fill="none"
            strokeLinecap="round"
          />
        </g>
      )}

      {peakDeg != null && (
        <path
          d={arcPath(FACE.cx, FACE.cy, radius, a0, peakDeg)}
          stroke="var(--accent)"
          strokeOpacity={0.5}
          strokeWidth={9}
          fill="none"
          strokeLinecap="round"
          style={{ filter: 'drop-shadow(0 0 6px var(--accent))' }}
        />
      )}

      {/* +dB region of the S-scale always reads in accent, even when not the
          active scale, so the operator can see at a glance where they are.
          When active it glows harder — a bloomed copy under the crisp arc. */}
      {isS && (
        <path
          d={arcPath(FACE.cx, FACE.cy, radius, normToDeg(scale.n(9)), a1)}
          stroke="var(--accent)"
          strokeOpacity={active ? 1 : 0.5}
          strokeWidth={active ? 9 : trackWidth + 0.5}
          fill="none"
          strokeLinecap="round"
          style={active ? { filter: 'drop-shadow(0 0 7px var(--accent))' } : undefined}
        />
      )}

      {scale.ticks.map((t, i) => {
        const ang = tickAngle(t.v);
        const len = t.major ? 12 : 6;
        const [x0, y0] = pt(FACE.cx, FACE.cy, radius - 1, ang);
        const [x1, y1] = pt(FACE.cx, FACE.cy, radius + len, ang);
        const isPlus = t.plus;
        return (
          <line
            key={`tk-${i}`}
            x1={x0}
            y1={y0}
            x2={x1}
            y2={y1}
            stroke={isPlus ? 'var(--accent)' : trackColor}
            strokeOpacity={isPlus ? (active ? 1 : 0.7) : trackOpacity}
            strokeWidth={t.major ? 2 : 1}
          />
        );
      })}

      {scale.ticks.map((t, i) => {
        if (!t.label) return null;
        const ang = tickAngle(t.v);
        const lr = radius + (t.major ? 32 : 26);
        const [lx, ly] = pt(FACE.cx, FACE.cy, lr, ang);
        const isPlus = t.plus;
        return (
          <text
            key={`lb-${i}`}
            x={lx}
            y={ly}
            fill={isPlus ? 'var(--accent)' : labelColor}
            opacity={isPlus ? (active ? 1 : 0.85) : labelOpacity}
            fontSize={t.major ? 24 : 19}
            fontWeight={t.major ? 800 : 700}
            fontFamily="var(--font-sans)"
            textAnchor="middle"
            dominantBaseline="middle"
          >
            {t.label}
          </text>
        );
      })}

      {/* Scale label on the left (S, PO, SWR). */}
      {(() => {
        const [lx, ly] = pt(FACE.cx, FACE.cy, radius + 6, a0 - 2.5);
        return (
          <text
            x={lx - 18}
            y={ly}
            fill={active ? 'var(--fg-0)' : 'var(--fg-1)'}
            opacity={active ? 1 : 0.85}
            fontSize={20}
            fontWeight={800}
            fontFamily="var(--font-sans)"
            letterSpacing="0.08em"
            textAnchor="end"
            dominantBaseline="middle"
          >
            {scale.label}
          </text>
        );
      })()}

      {active && scale.unit && (() => {
        const tip = scale.ticks[scale.ticks.length - 1];
        if (!tip) return null;
        const ang = tickAngle(tip.v) + 2;
        const [ux, uy] = pt(FACE.cx, FACE.cy, radius + 32, ang);
        return (
          <text
            x={ux + 4}
            y={uy}
            fill="var(--accent)"
            fontSize={20}
            fontWeight={800}
            fontFamily="var(--font-sans)"
            textAnchor="start"
            dominantBaseline="middle"
          >
            {scale.unit}
          </text>
        );
      })()}
    </g>
  );
}

interface NeedleProps {
  angleDeg: number;
  peakAngleDeg: number | null;
  /** Ref to the rotating needle <g> so the 60 Hz glide can drive its rotate
   *  transform imperatively (no setState in the hot path). */
  needleGroupRef: React.RefObject<SVGGElement | null>;
}

function Needle({ angleDeg, peakAngleDeg, needleGroupRef }: NeedleProps) {
  const r = FACE.rOuter + 30;
  const tail = -55;
  const [tx, ty] = pt(FACE.cx, FACE.cy, r, 0);
  const [bx, by] = pt(FACE.cx, FACE.cy, tail, 0);

  // Tapered metal blade as a filled polygon — wide at the hub, narrowing to
  // a fine tip. ~9 user-units wide at the base (the SVG is 1000 wide so this
  // reads as a chunky machined pointer, not a hairline).
  const BASE_HALF = 4.5;
  const blade = `M ${FACE.cx - BASE_HALF} ${FACE.cy} L ${tx} ${ty} L ${FACE.cx + BASE_HALF} ${FACE.cy} Z`;
  const tipFrac = 0.55;
  const tipX = FACE.cx + (tx - FACE.cx) * tipFrac;
  const tipY = FACE.cy + (ty - FACE.cy) * tipFrac;

  return (
    <Fragment>
      {peakAngleDeg != null && (
        <g transform={`rotate(${peakAngleDeg} ${FACE.cx} ${FACE.cy})`}>
          <line
            x1={FACE.cx}
            y1={FACE.cy}
            x2={tx}
            y2={ty}
            stroke="var(--accent)"
            strokeOpacity={0.45}
            strokeWidth={2}
          />
        </g>
      )}

      <g ref={needleGroupRef} transform={`rotate(${angleDeg} ${FACE.cx} ${FACE.cy})`}>
        {/* Soft drop shadow under the blade. */}
        <path d={`M ${FACE.cx - BASE_HALF} ${FACE.cy + 3} L ${tx} ${ty + 3} L ${FACE.cx + BASE_HALF} ${FACE.cy + 3} Z`} fill="#000" opacity={0.4} />
        {/* Counterweight below the pivot. */}
        <line
          x1={FACE.cx}
          y1={FACE.cy}
          x2={bx}
          y2={by}
          stroke="var(--tx)"
          strokeOpacity={0.7}
          strokeWidth={5}
          strokeLinecap="round"
        />
        {/* Main tapered blade. */}
        <path d={blade} fill="var(--tx)" />
        {/* Dark bevel along the trailing (right) edge — the Lambert shadow
            side, scaled up to a real 1.6-unit edge. */}
        <path
          d={`M ${FACE.cx + BASE_HALF} ${FACE.cy} L ${tx} ${ty} L ${tx - 1.6} ${ty} L ${FACE.cx + BASE_HALF - 1.6} ${FACE.cy} Z`}
          fill="var(--needle-bevel-lo)"
        />
        {/* Bright bevel along the leading (left) edge — the highlight side. */}
        <path
          d={`M ${FACE.cx - BASE_HALF} ${FACE.cy} L ${tx} ${ty} L ${tx + 1.6} ${ty} L ${FACE.cx - BASE_HALF + 1.6} ${FACE.cy} Z`}
          fill="var(--needle-bevel-hi)"
          opacity={0.9}
        />
        {/* Bright glowing tip. */}
        <line
          x1={tipX}
          y1={tipY}
          x2={tx}
          y2={ty}
          stroke="var(--power)"
          strokeWidth={4}
          strokeLinecap="round"
          style={{ filter: 'drop-shadow(0 0 6px var(--power))' }}
        />
      </g>

      {/* Lit jewel pivot — drawn last so it sits above the blade. A machined
          chrome collar, a radial-gradient jewel cap, and a bright glowing
          core (like BigArc's hub). */}
      <circle cx={FACE.cx} cy={FACE.cy} r={14} fill="var(--bg-3)" stroke="var(--panel-edge)" strokeWidth={2} />
      <circle cx={FACE.cx} cy={FACE.cy} r={11} fill="url(#analogMeterJewel)" />
      <circle
        cx={FACE.cx}
        cy={FACE.cy}
        r={5}
        fill="var(--power)"
        style={{ filter: 'drop-shadow(0 0 5px var(--power)) drop-shadow(0 0 9px var(--tx-soft))' }}
      />
    </Fragment>
  );
}

interface NeedleShadowProps {
  angleDeg: number;
}

function NeedleShadow({ angleDeg }: NeedleShadowProps) {
  const r = FACE.rOuter + 18;
  const half = FACE.sweep / 2;
  const startA = -half;
  const endA = angleDeg;
  if (endA <= startA + 0.5) return null;
  const [x0, y0] = pt(FACE.cx, FACE.cy, r, startA);
  const [x1, y1] = pt(FACE.cx, FACE.cy, r, endA);
  const d = `M ${FACE.cx} ${FACE.cy} L ${x0} ${y0} A ${r} ${r} 0 0 1 ${x1} ${y1} Z`;
  return (
    <>
      {/* Bloomed copy under the crisp wedge so the sweep glows like the arc
          fills on the immersive gauges — brightened for the dramatic dial. */}
      <path d={d} fill="url(#analogMeterSweepGrad)" opacity={0.22} filter="url(#analogMeterSweepBlur)" />
      <path d={d} fill="url(#analogMeterSweepGrad)" opacity={0.30} />
    </>
  );
}

export interface AnalogMeterFaceProps {
  enabledScales: Record<ScaleId, boolean>;
  activeScaleId: ScaleId;
  /** Needle position as 0..1 against the active scale. */
  needleN: number;
  /** Peak-hold position as 0..1 against the active scale, or null to suppress. */
  peakN: number | null;
  /** Per-render scale set; defaults to the canonical SCALES. The panel
   *  passes a customised set so operator tick selections + PO full-scale
   *  changes feed through without mutating module-global state. */
  scales?: Record<ScaleId, ScaleDef>;
}

export function AnalogMeterFace({
  enabledScales,
  activeScaleId,
  needleN,
  peakN,
  scales = SCALES,
}: AnalogMeterFaceProps) {
  const clampedN = Math.max(0, Math.min(1, needleN));
  const peakAngle = peakN != null ? normToDeg(Math.max(0, Math.min(1, peakN))) : null;

  // 60 Hz display-side glide for the needle. We glide the already-normalised
  // needleN (BEFORE normToDeg), then drive the needle <g> rotate transform
  // imperatively so the sweep is liquid at up to 60 Hz between the 30 Hz
  // value publishes — without re-rendering this subtree in the hot path. The
  // peak ghost/arc stays INSTANT (driven by peakN, never glided). normToDeg
  // and the needleN VALUE are unchanged — only the drawn rotation eases.
  const needleGroupRef = useRef<SVGGElement | null>(null);
  const needleGlide = useGlidedFraction(clampedN, {
    onDraw: (drawn) => {
      const g = needleGroupRef.current;
      if (g) g.setAttribute('transform', `rotate(${normToDeg(drawn).toFixed(3)} ${FACE.cx} ${FACE.cy})`);
    },
  });
  // The needle's React-rendered initial angle must track the GLIDED drawn
  // value (not the raw target) so a 30 Hz re-render doesn't snap the needle
  // ahead of the spring — the bus then continues easing from here. The sweep
  // wedge shares this drawn angle. Peak stays on the raw target angle.
  const drawnAngle = normToDeg(needleGlide.read());

  // Concentric radii: active scale takes the outermost slot; remaining enabled
  // scales render below it in a fixed display order (s, po, swr).
  const order: ScaleId[] = (['s', 'po', 'swr'] as const).filter((id) => enabledScales[id]);
  const radii: Partial<Record<ScaleId, number>> = {};
  let r = FACE.rOuter;
  radii[activeScaleId] = r;
  r -= FACE.arcGap;
  for (const id of order) {
    if (id === activeScaleId) continue;
    radii[id] = r;
    r -= FACE.arcGap;
  }

  return (
    <div className="analog-meter-face-wrap">
      <svg
        className="analog-meter-face"
        viewBox={`0 ${FACE.h * 0.05} ${FACE.w} ${FACE.h - FACE.h * 0.05}`}
        preserveAspectRatio="xMidYMid meet"
      >
        <defs>
          {/* Deep recessed dial well — a concave multi-stop radial floor
              (--meter-well-edge rim → --meter-well-floor centre) so the dial
              reads machined into the chassis, not a flat panel. Anchored at
              the upper-left so the whole instrument is lit from one key. */}
          <radialGradient id="analogMeterDialBg" cx="38%" cy="20%" r="95%">
            <stop offset="0%" stopColor="var(--meter-well-edge)" />
            <stop offset="55%" stopColor="var(--meter-well-floor)" />
            <stop offset="100%" stopColor="var(--meter-well-floor)" />
          </radialGradient>
          <linearGradient id="analogMeterSweepGrad" x1="0" x2="0" y1="1" y2="0">
            <stop offset="0%" stopColor="var(--accent)" stopOpacity="0" />
            <stop offset="100%" stopColor="var(--accent)" stopOpacity="0.85" />
          </linearGradient>
          {/* Domed-glass specular — an upper-left radial hot-spot (the pow()
              specular analogue) over the dial face so the instrument reads as
              covered by curved glass. */}
          <radialGradient id="analogMeterGlass" cx="30%" cy="14%" r="70%">
            <stop offset="0%" stopColor="var(--meter-glass-dome)" />
            <stop offset="40%" stopColor="var(--meter-glass-top)" />
            <stop offset="100%" stopColor="var(--meter-glass-bot)" />
          </radialGradient>
          {/* moving-sheen band gradient — a soft bright vertical streak. */}
          <linearGradient id="analogMeterSheen" x1="0" x2="1" y1="0" y2="0">
            <stop offset="0%" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
            <stop offset="50%" stopColor="var(--meter-sheen)" />
            <stop offset="100%" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
          </linearGradient>
          {/* Lit jewel pivot cap — a domed gem catching the upper-left key. */}
          <radialGradient id="analogMeterJewel" cx="38%" cy="30%" r="75%">
            <stop offset="0%" stopColor="rgba(255,255,255,0.7)" />
            <stop offset="35%" stopColor="var(--tx)" />
            <stop offset="100%" stopColor="#3a0a06" />
          </radialGradient>
          {/* Soft blur for the colored sweep wedge so it glows like the arc
              fills on the immersive gauges. */}
          <BloomFilter id="analogMeterSweepBlur" stdDeviation={4} />
        </defs>

        {/* Deep recessed dial floor. */}
        <rect x="0" y="0" width={FACE.w} height={FACE.h} fill="url(#analogMeterDialBg)" />
        {/* Inner-shadow vignette around the rim so the floor reads concave. */}
        <rect
          x="0"
          y="0"
          width={FACE.w}
          height={FACE.h}
          fill="none"
          stroke="rgba(0,0,0,0.6)"
          strokeWidth={28}
          opacity={0.5}
          style={{ filter: 'blur(14px)' }}
        />

        {/* Thick anodised bezel arc — concentric chrome framing the dial.
            Drawn along the outermost scale radius + a hair beyond so the dial
            sits IN a machined ring. */}
        <GaugeBezel
          variant="arc"
          defsId="amface-bezel"
          d={arcPath(FACE.cx, FACE.cy, FACE.rOuter + 56, -FACE.sweep / 2 - 4, FACE.sweep / 2 + 4)}
          thickness={10}
        />

        {(['s', 'po', 'swr'] as const).map((id) => {
          const radius = radii[id];
          if (radius == null) return null;
          return (
            <ScaleArc
              key={id}
              scale={scales[id]}
              radius={radius}
              active={id === activeScaleId}
              enabled={enabledScales[id]}
              peakValueN={id === activeScaleId ? peakN : null}
            />
          );
        })}

        <NeedleShadow angleDeg={drawnAngle} />
        <Needle angleDeg={drawnAngle} peakAngleDeg={peakAngle} needleGroupRef={needleGroupRef} />

        {/* Domed glass cover drawn LAST so it reads as curved glass over the
            whole instrument — an upper-left specular hot-spot plus a slow
            drifting sheen band (compositor-only CSS, paused under reduced
            motion). pointer-events:none so it never blocks interaction. */}
        <g style={{ pointerEvents: 'none' }}>
          <rect x="0" y="0" width={FACE.w} height={FACE.h * 0.62} fill="url(#analogMeterGlass)" />
          <g
            className="meter-sheen-drift"
            style={{ ['--sheen-travel' as string]: `${FACE.w * 1.25}px` }}
          >
            <rect x={-FACE.w * 0.2} y="0" width={FACE.w * 0.16} height={FACE.h} fill="url(#analogMeterSheen)" />
          </g>
        </g>
      </svg>
    </div>
  );
}
